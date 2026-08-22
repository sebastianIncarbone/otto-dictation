using Microsoft.Extensions.Logging;
using Otto.Core;

namespace Otto.PostProcessing;

/// <summary>
/// Corrects the transcription in-process via LLamaSharp. Never a network call —
/// the model lives in this process's own memory — and entirely optional: if it
/// fails to load, Otto disables the feature and dictation carries on with
/// Whisper's raw output.
/// </summary>
public sealed class LlamaPostProcessor : IPostProcessor, IDisposable
{
    // VoseoPrompt's system message plus its few-shot examples costs ~700 tokens
    // fixed, and the model's own output is capped at options.MaxTokens (512 by
    // default). That leaves ~1,024 tokens of the 4096-token context for the
    // dictation itself — see PostProcessingOptions.ContextSize. ~3 characters per
    // Spanish BPE token is a rough estimate (measured against VoseoPrompt's own
    // ~1,720 chars ≈ ~570 tokens); an exact count would need the model's own
    // tokenizer loaded, which defeats the point of rejecting oversize input
    // before paying for inference.
    private const int MaxInputTokens = 1024;
    private const double CharactersPerToken = 3.0;

    private readonly ICorrectionEngine engine;
    private readonly PostProcessingOptions options;
    private readonly ILogger<LlamaPostProcessor> log;
    private readonly SemaphoreSlim gate = new(1, 1);

    // Set ONLY after a load fully succeeds (engine.LoadAsync AND WarmUpAsync
    // both returned without throwing) — never in a catch/failure path. That
    // asymmetry is the whole fix: it is what makes ProbeAsync idempotent on
    // success (a Ready processor never reloads) while staying retryable after
    // a failure or a ProbeTimeout cancellation, which is what the tray's
    // "reintentar" action depends on to recover without restarting Otto.
    private bool loadSucceeded;

    public LlamaPostProcessor(ICorrectionEngine engine, PostProcessingOptions options, ILogger<LlamaPostProcessor> log)
    {
        this.engine = engine;
        this.options = options;
        this.log = log;
    }

    public bool IsAvailable { get; private set; }

    /// <summary>
    /// Ensures the model is loaded, gated by a <see cref="SemaphoreSlim"/> so
    /// concurrent callers — the startup probe racing a tray "reintentar" click,
    /// for instance — never run two loads at once; the second caller waits for
    /// the in-flight attempt and observes its outcome instead of starting its own.
    ///
    /// Idempotent ONLY on success: once a load succeeds, every later call
    /// returns immediately without touching the gate or reloading. A failed or
    /// canceled/timed-out load is deliberately NOT sticky — it is a normal,
    /// expected outcome (CPU-only hardware, a missing GGUF, an unsupported
    /// Vulkan driver, a hung native call past <see cref="PostProcessingOptions.ProbeTimeout"/>)
    /// and the next call retries the load from scratch. Without this asymmetry
    /// a single failed load would permanently disable the tray's recovery path
    /// for the rest of the process's life.
    /// </summary>
    public async Task<bool> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (loadSucceeded) return IsAvailable;

        await gate.WaitAsync(cancellationToken);

        try
        {
            // Re-check inside the gate: another caller may have already
            // finished a successful load while this one was waiting for it.
            if (loadSucceeded) return IsAvailable;

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(options.ProbeTimeout);

            await engine.LoadAsync(budget.Token);
            log.LogInformation("Correction model loaded");

            // IsAvailable flips only AFTER warm-up returns — including when its
            // own failure is swallowed inside WarmUpAsync — not right after
            // LoadAsync. ProcessAsync's only gate is IsAvailable, and the engine's
            // InteractiveExecutor/LLamaContext is not reentrant: flipping it early
            // let a dictation land in this exact window and call ChatAsync
            // concurrently with warm-up's own in-flight ChatAsync against the
            // SAME native handle. With Ollama this was harmless — independent
            // HTTP requests — but in-process it was a real race.
            await WarmUpAsync(budget.Token);
            IsAvailable = true;
            loadSucceeded = true;
        }
        catch (Exception ex)
        {
            // A failed load is a perfectly normal way to run Otto — CPU-only
            // hardware, a missing GGUF, an unsupported Vulkan driver, or a
            // ProbeTimeout cancellation. loadSucceeded stays false (see the
            // field's doc comment) so the NEXT ProbeAsync call retries instead
            // of finding a permanently stuck gate.
            IsAvailable = false;
            log.LogInformation(ex, "Correction model could not be loaded; using the raw transcription");
        }
        finally
        {
            gate.Release();
        }

        return IsAvailable;
    }

    public async Task<string> ProcessAsync(
        string text,
        DictationContext context,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(text)) return text;

        if (EstimateTokens(text) > MaxInputTokens)
        {
            log.LogWarning(
                "Dictation is too long for the correction context ({Chars} chars); inserting the raw text",
                text.Length);

            return text;
        }

        // The budget belongs to this call, not to the engine: past it the raw
        // transcription goes in rather than making the user wait longer.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.Timeout);

        try
        {
            var corrected = await ChatAsync(text, budget.Token);

            if (!EditGuard.IsSafe(text, corrected))
            {
                log.LogWarning(
                    "Correction discarded: it touched {Touched} words. Inserting the raw text",
                    EditGuard.WordsTouched(text, corrected));

                return text;
            }

            return corrected;
        }
        catch (OperationCanceledException)
        {
            log.LogWarning("Post-processing took longer than {Seconds:F0} s; inserting the raw text",
                options.Timeout.TotalSeconds);

            return text;
        }
        catch (Exception ex)
        {
            // Never let a correction cost someone their dictation.
            log.LogWarning(ex, "Post-processing failed; inserting the raw text");
            return text;
        }
    }

    /// <summary>
    /// Loads the model into VRAM before the first dictation needs it.
    ///
    /// Measured cold on the Ollama path, the correction took longer than its
    /// entire two-second budget and got discarded every time — the feature looked
    /// broken while being perfectly healthy. Warm, it took about a third of a
    /// second. Paying that cost once here is the same trick the speech model
    /// needs for Vulkan. Its own failure is swallowed: a failed warm-up costs the
    /// first dictation some latency, not the whole feature — the model already
    /// loaded successfully by the time this runs.
    /// </summary>
    private async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await ChatAsync("Hola.", cancellationToken);
            log.LogInformation("Correction model warmed up in {Seconds:F1} s", watch.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not warm up the correction model");
        }
    }

    private Task<string> ChatAsync(string text, CancellationToken cancellationToken)
    {
        var messages = new List<(string Role, string Content)> { ("system", VoseoPrompt.System) };

        foreach (var (input, output) in VoseoPrompt.Examples)
        {
            messages.Add(("user", input));
            messages.Add(("assistant", output));
        }

        messages.Add(("user", text));

        return engine.ChatAsync(messages, cancellationToken);
    }

    private static int EstimateTokens(string text) => (int)Math.Ceiling(text.Length / CharactersPerToken);

    /// <summary>
    /// Forwards to the engine, which is where the native GGUF/Vulkan handles
    /// actually live. Not every <see cref="ICorrectionEngine"/> owns
    /// anything to release — the test doubles in this project's own tests
    /// don't — so the cast is deliberately soft rather than a hard
    /// downcast. This call site does NOT go through <see cref="gate"/> —
    /// production's <c>LlamaEngine</c> is what keeps that safe on its own end:
    /// a load still in flight (its own <c>LoadGenerationTracker</c>) and a
    /// correction or warm-up still in flight (its own <c>InFlightGate</c>,
    /// which is what <see cref="gate"/> bypassing would otherwise have made
    /// unsafe — WarmUpAsync runs the very same <c>ChatAsync</c> a real
    /// correction does, while holding <see cref="gate"/>, and this method
    /// never waits on it) are both handled there. This method only needs to
    /// reach it.
    /// </summary>
    public void Dispose() => (engine as IDisposable)?.Dispose();
}

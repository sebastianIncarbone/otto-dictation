using Microsoft.Extensions.Logging;
using Otto.Core;

namespace Otto.PostProcessing;

/// <summary>
/// Corrects the transcription in-process via LLamaSharp. Never a network call —
/// the model lives in this process's own memory — and entirely optional: if it
/// fails to load, Otto disables the feature and dictation carries on with
/// Whisper's raw output.
/// </summary>
public sealed class LlamaPostProcessor : IPostProcessor
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

    private bool loaded;

    public LlamaPostProcessor(ICorrectionEngine engine, PostProcessingOptions options, ILogger<LlamaPostProcessor> log)
    {
        this.engine = engine;
        this.options = options;
        this.log = log;
    }

    public bool IsAvailable { get; private set; }

    /// <summary>
    /// Idempotent ensure-loaded, not a connectivity check: the model loads (and
    /// warms up) at most once per instance. Gated so concurrent callers — the
    /// startup probe racing a tray "reintentar" click, for instance — load exactly
    /// once instead of each paying for their own Vulkan context.
    /// </summary>
    public async Task<bool> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (loaded) return IsAvailable;

        await gate.WaitAsync(cancellationToken);

        try
        {
            if (loaded) return IsAvailable;

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(options.ProbeTimeout);

            await engine.LoadAsync(budget.Token);
            IsAvailable = true;
            log.LogInformation("Correction model loaded");

            await WarmUpAsync(budget.Token);
        }
        catch (Exception ex)
        {
            // A failed load is a perfectly normal way to run Otto — CPU-only
            // hardware, a missing GGUF, an unsupported Vulkan driver.
            IsAvailable = false;
            log.LogInformation(ex, "Correction model could not be loaded; using the raw transcription");
        }
        finally
        {
            loaded = true;
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
}

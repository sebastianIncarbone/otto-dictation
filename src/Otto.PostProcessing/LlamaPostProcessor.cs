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

    // Backing fields for the three properties below, all funneled through the
    // SAME change-detecting setter (SetIfChanged) so "did the observable state
    // actually change" is decided in exactly one tested place rather than by
    // hand at the end of ProbeAsync, UnloadAsync, SetEnabledAsync and
    // OnIdleExpired separately.
    private bool isAvailable;
    private bool enabledField;
    private bool idleUnloadedField;

    private readonly IdleUnloadScheduler idle;

    public LlamaPostProcessor(
        ICorrectionEngine engine,
        PostProcessingOptions options,
        ILogger<LlamaPostProcessor> log,
        bool enabled = true,
        TimeProvider? clock = null)
    {
        this.engine = engine;
        this.options = options;
        this.log = log;
        Enabled = enabled;

        idle = new IdleUnloadScheduler(clock ?? TimeProvider.System, OnIdleExpired, options.IdleUnloadInterval);
    }

    /// <summary>
    /// True once the correction model has loaded and warmed up. Setting this
    /// to a value it does not already hold raises <see cref="AvailabilityChanged"/> —
    /// see <see cref="SetIfChanged"/>.
    /// </summary>
    public bool IsAvailable
    {
        get => isAvailable;
        private set => SetIfChanged(ref isAvailable, value);
    }

    /// <summary>
    /// Whether the user currently wants correction on — the Settings
    /// checkbox and the tray toggle both flow through <see cref="SetEnabledAsync"/>,
    /// which is the only thing that changes this. Distinct from
    /// <see cref="IsAvailable"/>: the model can be enabled but not yet
    /// loaded (a fresh <see cref="ProbeAsync"/> still in flight, or a
    /// previous load failed), and — briefly, until <see cref="LlamaEngine.UnloadAsync"/>
    /// gives up rather than freeing memory under a live call — it can even
    /// stay <see cref="IsAvailable"/> after being switched off.
    /// </summary>
    public bool Enabled
    {
        get => enabledField;
        private set => SetIfChanged(ref enabledField, value);
    }

    /// <inheritdoc cref="IPostProcessor.IdleUnloaded"/>
    public bool IdleUnloaded
    {
        get => idleUnloadedField;
        private set => SetIfChanged(ref idleUnloadedField, value);
    }

    /// <inheritdoc cref="IPostProcessor.AvailabilityChanged"/>
    public event Action? AvailabilityChanged;

    /// <summary>
    /// The one place that decides "did the observable state actually
    /// change" — <see cref="IsAvailable"/>, <see cref="Enabled"/> and
    /// <see cref="IdleUnloaded"/> all funnel through this instead of each
    /// raising <see cref="AvailabilityChanged"/> unconditionally at the end
    /// of ProbeAsync/UnloadAsync/SetEnabledAsync/OnIdleExpired, which would
    /// notify a subscriber even when nothing the tray shows actually moved
    /// (a ProbeAsync call that finds the model already loaded, say).
    /// </summary>
    private void SetIfChanged(ref bool field, bool value)
    {
        if (field == value) return;

        field = value;
        AvailabilityChanged?.Invoke();
    }

    /// <summary>
    /// Ensures the model is loaded, gated by a <see cref="SemaphoreSlim"/> so
    /// concurrent callers — the startup probe racing a tray "reintentar" click,
    /// for instance — never run two loads at once; the second caller waits for
    /// the in-flight attempt and observes its outcome instead of starting its own.
    ///
    /// Idempotent ONLY on success: once a load succeeds, every later call still
    /// takes the gate — briefly, since the check right inside it is the only
    /// thing that runs — but returns without reloading. It used to short-circuit
    /// on an UN-gated fast path here instead (read <c>loadSucceeded</c> before
    /// ever calling <c>gate.WaitAsync</c>), which was a real bug: a concurrent
    /// <see cref="UnloadAsync"/> that had already passed ITS OWN re-check but
    /// was still awaiting <c>engine.UnloadAsync</c> left <c>loadSucceeded</c>
    /// true for a little longer, and a <see cref="ProbeAsync"/> call landing in
    /// exactly that window read the stale value, decided nothing needed to
    /// happen, and returned — while the model was seconds away from actually
    /// being freed underneath it. <see cref="SetEnabledAsync"/>'s caller would
    /// then believe the reload it just asked for had already happened, leaving
    /// <see cref="Enabled"/> true but the model unloaded until a restart. The
    /// gate is what makes this call observe the CURRENT state instead of a
    /// snapshot taken before some other caller finished changing it.
    ///
    /// A failed or canceled/timed-out load is deliberately NOT sticky — it is
    /// a normal, expected outcome (CPU-only hardware, a missing GGUF, an
    /// unsupported Vulkan driver, a hung native call past
    /// <see cref="PostProcessingOptions.ProbeTimeout"/>) and the next call
    /// retries the load from scratch. Without this asymmetry a single failed
    /// load would permanently disable the tray's recovery path for the rest
    /// of the process's life.
    /// </summary>
    public async Task<bool> ProbeAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            // The only check left — an UN-gated one used to sit above this
            // method too (see its own doc comment for the exact race that
            // closed). Every call now takes the gate for real, so this is the
            // sole place loadSucceeded/IsAvailable are read to decide what
            // happens next; another caller may have already finished a
            // successful load while this one was waiting for the gate.
            if (loadSucceeded) return IsAvailable;

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(options.ProbeTimeout);

            await engine.LoadAsync(budget.Token);
            log.LogInformation("Correction model loaded");

            // IsAvailable flips only AFTER warm-up returns — including when its
            // own failure is swallowed inside WarmUpAsync — not right after
            // LoadAsync. ProcessAsync's only gate is IsAvailable, and the engine's
            // native executor is not reentrant: flipping it early let a
            // dictation land in this exact window and call ChatAsync
            // concurrently with warm-up's own in-flight ChatAsync against the
            // SAME native handle. With Ollama this was harmless — independent
            // HTTP requests — but in-process it was a real race.
            await WarmUpAsync(budget.Token);
            IsAvailable = true;
            loadSucceeded = true;
            IdleUnloaded = false;

            // Starts the idle clock the moment the model actually becomes
            // usable — a freshly loaded model gets its own full idle window
            // rather than inheriting whatever was left over from before.
            idle.Touch();
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
        if (!IsAvailable || !Enabled || string.IsNullOrWhiteSpace(text))
        {
            // The `!Enabled` check above is separate from the IdleUnloaded
            // one below and exists for a different reason: IsAvailable can
            // briefly stay true after Enabled goes false — a queued
            // UnloadAsync waiting behind an in-flight ProbeAsync's gate hold,
            // or the engine refusing to free memory under a live call (see
            // UnloadAsync's own doc comment for both) — and without this, a
            // dictation landing in that window would still get corrected by
            // a model the user just switched off. A disabled corrector must
            // never correct, whatever IsAvailable happens to say.
            //
            // IdleUnloaded, not Enabled alone, decides whether to trigger a
            // background RELOAD here: a model that has NEVER successfully
            // loaded (missing GGUF, unsupported Vulkan driver) must not get a
            // fresh ProbeAsync attempt behind every single dictation — that
            // would turn this into the exact per-dictation health check
            // DictationPipeline's own deferred-load design already rejects,
            // just hidden one layer down. Only an idle unload — a model that
            // DID work a moment ago — earns the background retry;
            // SetEnabledAsync(true) reloads directly instead of waiting for
            // this branch to notice.
            if (IdleUnloaded && Enabled && !string.IsNullOrWhiteSpace(text))
                _ = ProbeAsync(CancellationToken.None);

            return text;
        }

        if (EstimateTokens(text) > MaxInputTokens)
        {
            log.LogWarning(
                "Dictation is too long for the correction context ({Chars} chars); inserting the raw text",
                text.Length);

            return text;
        }

        // Counts as activity: a machine dictating every few minutes must not
        // lose the model between two of them just because the idle window is
        // shorter than the gap the user actually leaves.
        idle.Touch();

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
    /// Turns correction on or off at runtime — what the Settings checkbox
    /// and the tray toggle both call, mirroring the same two-owner pattern
    /// <c>App.SetCharacterVisible</c> already uses for the character
    /// overlay. Enabling loads (and warms up) the model if it is not
    /// already available; disabling frees its native handles while leaving
    /// this processor able to load again. A no-op past the first call with
    /// the same value — toggling the same state twice must not repeat a
    /// multi-second load or a native free.
    /// </summary>
    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (Enabled == enabled) return;

        // Raises AvailabilityChanged synchronously, right here — before the
        // load or unload below has even started. That is deliberate: it is
        // what lets a caller (App.axaml.cs's tray/Settings handler) refresh
        // its own display immediately, rather than only after ProbeAsync or
        // UnloadAsync settles, which — bounded by ProbeTimeout — can take up
        // to a minute on a slow or hung load.
        Enabled = enabled;

        if (enabled)
        {
            await ProbeAsync(cancellationToken);
        }
        else
        {
            idle.Stop();
            await UnloadAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Reconfigures the idle-unload interval — what a Settings save or a
    /// tray change calls. Null means "never unload".
    /// </summary>
    public void SetIdleTimeout(TimeSpan? interval) => idle.Configure(interval);

    /// <summary>
    /// The reversible half of <see cref="Dispose"/> — frees the model's
    /// native handles while leaving this processor able to load again.
    /// Called by the idle timer and by <see cref="SetEnabledAsync"/>'s own
    /// disable path; guarded by the SAME <see cref="gate"/> <see cref="ProbeAsync"/>
    /// uses, so the two can never run concurrently against each other — a
    /// concurrent unload could otherwise free memory out from under a
    /// warm-up that had already published its weights but had not yet
    /// finished its own in-flight <c>ChatAsync</c> call, the exact race
    /// <c>LlamaEngine</c>'s own <c>InFlightGate</c> exists to close one
    /// layer down, closed here one layer up instead by simply never letting
    /// the two bodies overlap in the first place.
    /// </summary>
    public async Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            // An UN-gated fast path used to sit here too
            // (`if (!loadSucceeded) return;`, read before ever touching the
            // gate): a disable landing while a concurrent ProbeAsync was
            // still mid-load — loadSucceeded still false at that exact
            // instant, since it only flips true after WarmUpAsync finishes —
            // returned immediately as a no-op instead of waiting for the load
            // ProbeAsync's own gate hold was about to publish. The probe then
            // finished normally and left the model resident, with nothing
            // left that would ever unload it. Every call now blocks on the
            // SAME gate ProbeAsync holds for its entire body, so a disable
            // that lands mid-load waits for that load to settle and THEN
            // frees it, rather than racing past it and losing the request.
            if (!loadSucceeded) return;

            var freed = await engine.UnloadAsync(cancellationToken);

            if (!freed)
            {
                // The engine gave up rather than free memory under a live
                // call (InFlightGate.TryUnload's own bounded wait) — nothing
                // actually changed, so nothing here should say otherwise. A
                // later ProbeAsync call would otherwise start a SECOND load
                // racing the still-resident weights from the first one.
                log.LogWarning("Correction model still in use; staying loaded instead of unloading underneath a live call");
                return;
            }

            IsAvailable = false;
            loadSucceeded = false;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The idle timer's own callback — fires once no correction (or load)
    /// has happened within <see cref="PostProcessingOptions.IdleUnloadInterval"/>.
    /// Marks <see cref="IdleUnloaded"/> BEFORE kicking off the unload itself,
    /// not after: <see cref="ProcessAsync"/>'s reload trigger only needs to
    /// know "this WAS an idle unload", not wait for the unload to finish. This
    /// assignment alone can raise <see cref="AvailabilityChanged"/> — while
    /// <see cref="IsAvailable"/> is still true, since the unload has not
    /// actually run yet — which is harmless: a subscriber re-reading the
    /// still-unchanged <see cref="IsAvailable"/> just repeats the same Ready
    /// state it already had.
    /// </summary>
    private void OnIdleExpired()
    {
        IdleUnloaded = true;
        _ = UnloadAsync(CancellationToken.None);
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
    public void Dispose()
    {
        // Stopped first: Dispose is terminal for the whole processor, not
        // just the engine, and a pending idle timer firing afterward would
        // call UnloadAsync against an engine that may already be gone.
        // Otto only calls Dispose right before process shutdown, so this is
        // hygiene more than a live bug — but it is cheap to guarantee
        // outright rather than rely on IdleUnloadScheduler firing into a
        // no-op.
        idle.Dispose();

        (engine as IDisposable)?.Dispose();
    }
}

namespace Otto.App;

/// <summary>
/// The states the tray's correction item can be in. There is deliberately no
/// "connecting"/"disconnected" pair here — that was the Ollama-era model, and an
/// in-process corrector has no connection to lose.
///
/// Hidden is not a member of this enum: it stays a decision the caller makes
/// before ever calling <see cref="CorrectionTrayStates.For"/>, exactly as before —
/// when <c>Settings.CorrectVoseo</c> is off the item is not built at all, so there
/// is nothing here to represent it. <see cref="Unsupported"/> is different: the
/// composition root (<c>App.axaml.cs</c>) gives CPU-only hardware that same
/// "hides what it can't do" treatment (see <c>ProvisioningOptions.HasGpu</c> and
/// <c>ProvisioningOptions.CorrectionCoordinates</c>, which already refuses to
/// download the GGUF there — a "reintentar" that can never succeed is worse than
/// no button), so in normal operation this value is never actually shown. It is
/// still a real member — not folded into the caller-side Hidden decision — because
/// <see cref="CorrectionTrayStates.For"/> must be provably, testably unable to
/// return a retryable state for that hardware, independent of whatever the
/// composition root's own gate happens to do.
/// </summary>
public enum CorrectionTrayStateKind
{
    /// <summary>The GGUF is on disk but the deferred load has not settled yet.</summary>
    Loading,

    /// <summary>Loaded and warmed up — <c>IPostProcessor.IsAvailable</c> is true.</summary>
    Ready,

    /// <summary>The GGUF is not on disk. "reintentar" here means re-provisioning it.</summary>
    Missing,

    /// <summary>The GGUF is on disk, the load settled, and it is still not available.</summary>
    Failed,

    /// <summary>
    /// This machine has no GPU. A 3B model can never land inside the 2s dictation
    /// budget on CPU, so correction is not merely unavailable right now — it can
    /// never become available on this hardware. Wins over every other input,
    /// including a (production-impossible) <c>isAvailable: true</c>: the point of
    /// this value is to guarantee <c>CanRetry</c> is false whenever <c>hasGpu</c>
    /// is false, not to describe what the corrector is currently doing.
    /// </summary>
    Unsupported,
}

/// <summary>
/// One state: what the tray item's header should read, and whether its click
/// handler does anything.
/// </summary>
/// <param name="Kind">Which state this is.</param>
/// <param name="Header">The exact Rioplatense text the tray item shows.</param>
/// <param name="CanRetry">
/// Whether clicking the item should do something. True for exactly two states —
/// Missing (re-provision, then load) and Failed (reload) — the single "reintentar"
/// action the design calls for, covering both recoverable states with one verb.
/// </param>
public readonly record struct CorrectionTrayState(CorrectionTrayStateKind Kind, string Header, bool CanRetry);

/// <summary>
/// Derives the correction tray item's state from what is actually observable,
/// rather than storing a state anywhere. Otto.Core's <c>IPostProcessor</c> stays a
/// plain boolean port on purpose (see the design's rejected "widen IsAvailable to
/// an enum" alternative) — this is where the UI's richer vocabulary is allowed to
/// live, and only here.
///
/// Pure and dependency-free: no <c>IPostProcessor</c>, no <c>ProvisioningOptions</c>,
/// no DI container. <c>App.axaml.cs</c> — the Avalonia composition-root surface
/// this project's tests cannot construct headlessly — is reduced to gathering the
/// four booleans below and reading <see cref="CorrectionTrayState.Header"/> back;
/// every branch of the actual decision is exercised by
/// <c>CorrectionTrayStatesTests</c> instead.
/// </summary>
public static class CorrectionTrayStates
{
    /// <param name="isAvailable"><c>IPostProcessor.IsAvailable</c> — loaded and warmed up.</param>
    /// <param name="modelFileExists">Whether the GGUF is on disk right now.</param>
    /// <param name="deferredLoadSettled">
    /// Whether a load attempt — the one <see cref="Otto.Core.DictationPipeline"/>
    /// fires in the background at startup, or a manual "reintentar" — has finished
    /// at least once. Before the first one finishes the item cannot yet tell
    /// "still loading" from "failed", so it reads as Loading rather than guessing.
    /// </param>
    /// <param name="hasGpu">
    /// <c>ProvisioningOptions.HasGpu</c>. Checked first, ahead of every other
    /// input: on CPU-only hardware correction can never load inside the 2s budget,
    /// so nothing else here — including a currently-loaded corrector, which cannot
    /// really coexist with <c>hasGpu: false</c> in production — should be able to
    /// produce a state that invites a retry.
    /// </param>
    public static CorrectionTrayState For(bool isAvailable, bool modelFileExists, bool deferredLoadSettled, bool hasGpu) =>
        !hasGpu
            ? new CorrectionTrayState(CorrectionTrayStateKind.Unsupported, "Corrección: requiere GPU", CanRetry: false)
            : isAvailable
                ? new CorrectionTrayState(CorrectionTrayStateKind.Ready, "Corrección: activa ✓", CanRetry: false)
                : !modelFileExists
                    ? new CorrectionTrayState(CorrectionTrayStateKind.Missing, "Corrección: falta el modelo — reintentar", CanRetry: true)
                    : !deferredLoadSettled
                        ? new CorrectionTrayState(CorrectionTrayStateKind.Loading, "Corrección: cargando…", CanRetry: false)
                        : new CorrectionTrayState(CorrectionTrayStateKind.Failed, "Corrección: no se pudo cargar — reintentar", CanRetry: true);
}

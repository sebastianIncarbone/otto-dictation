namespace Otto.App;

/// <summary>
/// The states the tray's correction item can be in. There is deliberately no
/// "connecting"/"disconnected" pair here — that was the Ollama-era model, and an
/// in-process corrector has no connection to lose.
///
/// Hidden is not a member of this enum: it stays a decision the caller makes
/// before ever calling <see cref="CorrectionTrayStates.For"/> — but, unlike
/// before the runtime toggle existed, that decision is now hardware-only. When
/// <c>Settings.CorrectVoseo</c> is off the item still has to exist and stay
/// clickable — see <see cref="Off"/> — because the user must be able to turn
/// correction back on from the tray without opening Ajustes. <see cref="Unsupported"/>
/// is what still gets the "hides what it can't do" treatment: the composition
/// root (<c>App.axaml.cs</c>) does not build the item at all on CPU-only
/// hardware (see <c>ProvisioningOptions.HasGpu</c> and
/// <c>ProvisioningOptions.CorrectionCoordinates</c>, which already refuses to
/// download the GGUF there — a "reintentar" that can never succeed is worse
/// than no button), so in normal operation this value is never actually shown.
/// It is still a real member — not folded into the caller-side Hidden decision
/// — because <see cref="CorrectionTrayStates.For"/> must be provably, testably
/// unable to return a retryable state for that hardware, independent of
/// whatever the composition root's own gate happens to do.
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
    /// The user switched correction off — <c>Settings.CorrectVoseo</c> is
    /// false — on hardware that DOES support it. Wins over Ready/Missing/
    /// Loading/Failed: the header has to reflect what the user just asked
    /// for even in the brief window right after the toggle click, before
    /// <c>LlamaPostProcessor.UnloadAsync</c> actually settles and
    /// <c>IsAvailable</c> catches up. Clicking here is what turns it back
    /// on — App.axaml.cs's click handler branches on this Kind directly,
    /// the same raw toggle the character overlay's own tray item already
    /// uses, rather than routing through <see cref="CorrectionTrayState.CanRetry"/>.
    /// </summary>
    Off,

    /// <summary>
    /// This machine has no GPU. A 3B model can never land inside the 2s dictation
    /// budget on CPU, so correction is not merely unavailable right now — it can
    /// never become available on this hardware. Wins over every other input,
    /// including a (production-impossible) <c>isAvailable: true</c>: the point of
    /// this value is to guarantee <c>CanRetry</c> is false whenever <c>hasGpu</c>
    /// is false, not to describe what the corrector is currently doing.
    /// </summary>
    Unsupported,

    /// <summary>
    /// The model loaded successfully at least once, then the idle timer unloaded
    /// it — <c>LlamaPostProcessor.IdleUnloaded</c>. Distinct from <see cref="Failed"/>
    /// on purpose: an idle unload is Otto's own feature working as designed, not a
    /// broken model, and the next dictation reloads it automatically
    /// (<c>LlamaPostProcessor.ProcessAsync</c>'s own background-reload trigger) —
    /// showing "no se pudo cargar — reintentar" here would be an outright lie.
    /// Wins over Missing/Loading/Failed the same way Ready does (the model has
    /// already proven it can load on this machine) but loses to Ready itself and
    /// to Off, which both win for their own, higher-priority reasons. Clicking
    /// here behaves like clicking Ready/Loading — it turns correction off — not
    /// like Missing/Failed's "reintentar", since there is nothing broken to retry.
    /// </summary>
    Idle,
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
    /// <param name="correctVoseo">
    /// <c>Settings.CorrectVoseo</c>'s CURRENT value, read live rather than
    /// baked into anything — the whole point of the runtime toggle this
    /// state exists for. Checked second, right after <paramref name="hasGpu"/>:
    /// the user's own "off" has to win over Ready/Missing/Loading/Failed/Idle the
    /// same way hardware support wins over everything, so the header never
    /// lies about what a click will do.
    /// </param>
    /// <param name="idleUnloaded">
    /// <c>IPostProcessor.IdleUnloaded</c> — the model loaded successfully
    /// before and the idle timer unloaded it since, distinct from a genuine
    /// load failure or a model that never loaded at all. Checked right after
    /// <paramref name="isAvailable"/>: an idle unload has already proven the
    /// model CAN load here, so it must never fall through to
    /// <see cref="CorrectionTrayStateKind.Missing"/> or
    /// <see cref="CorrectionTrayStateKind.Failed"/> just because
    /// <paramref name="deferredLoadSettled"/> is (permanently, by this point)
    /// true.
    /// </param>
    public static CorrectionTrayState For(
        bool isAvailable, bool modelFileExists, bool deferredLoadSettled, bool hasGpu, bool correctVoseo, bool idleUnloaded) =>
        !hasGpu
            ? new CorrectionTrayState(CorrectionTrayStateKind.Unsupported, "Corrección: requiere GPU", CanRetry: false)
            : !correctVoseo
                ? new CorrectionTrayState(CorrectionTrayStateKind.Off, "Corrección: apagada — activar", CanRetry: false)
                : isAvailable
                    ? new CorrectionTrayState(CorrectionTrayStateKind.Ready, "Corrección: activa ✓", CanRetry: false)
                    : idleUnloaded
                        ? new CorrectionTrayState(CorrectionTrayStateKind.Idle, "Corrección: en pausa — se reactiva al dictar", CanRetry: false)
                        : !modelFileExists
                            ? new CorrectionTrayState(CorrectionTrayStateKind.Missing, "Corrección: falta el modelo — reintentar", CanRetry: true)
                            : !deferredLoadSettled
                                ? new CorrectionTrayState(CorrectionTrayStateKind.Loading, "Corrección: cargando…", CanRetry: false)
                                : new CorrectionTrayState(CorrectionTrayStateKind.Failed, "Corrección: no se pudo cargar — reintentar", CanRetry: true);
}

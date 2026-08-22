using Otto.Core;

namespace Otto.App;

/// <summary>
/// Runs one correction on/off toggle and reports what settled — the LIVE
/// <see cref="IPostProcessor.Enabled"/>, never the <c>enabled</c> value the
/// call itself was asked to set.
///
/// <see cref="Otto.App.App.SetCorrectionEnabled"/> can be called several
/// times in rapid succession (round 2 made the tray item clickable while a
/// previous toggle is still in flight — Ready/Loading/Idle now all route to
/// a click, not just Off), and every call's <c>await postProcessor
/// .SetEnabledAsync(enabled, …)</c> serializes on <c>LlamaPostProcessor</c>'s
/// own <c>SemaphoreSlim</c> gate, which Microsoft documents as giving no
/// waiter-ordering guarantee. Before this existed, each call's tail wrote
/// its OWN closure-captured <c>enabled</c> to disk once its own await
/// resumed — harmless for one click, but wrong for N: whichever call's gate
/// happened to release LAST would win the write, even if an intervening
/// click had already asked for something else. <c>Enabled</c> itself was
/// never the problem — <c>SetEnabledAsync</c> flips it synchronously,
/// before its own await, so by the time ANY call's gate-queued work
/// finishes, <c>Enabled</c> already holds the MOST RECENT call's value (see
/// <c>LlamaPostProcessorTests</c>' "fires immediately on Enabled flip"
/// test for that half of the guarantee). Reading it here, after the await,
/// instead of the parameter the call started with, is what makes every
/// racing call's tail agree on the identical answer: <see cref="IPostProcessor.Enabled"/>
/// only ever holds one value at a time, so it does not matter which call's
/// tail runs last — they all read the same thing.
///
/// Kept apart from <c>App.axaml.cs</c> on purpose — no Avalonia type, no DI
/// container anywhere in it — so this decision is testable without the
/// composition root this project cannot construct headlessly, the same
/// reason <see cref="CorrectionTrayStates"/> lives beside it.
/// </summary>
public static class CorrectionToggleCoordinator
{
    /// <summary>
    /// Awaits the toggle, then returns <see cref="IPostProcessor.Enabled"/>
    /// as it stands once THIS call's own gate-queued operation finishes —
    /// what a caller should persist to disk and reflect in
    /// <c>MainViewModel</c>, not <paramref name="enabled"/> itself.
    /// </summary>
    public static async Task<bool> ToggleAsync(
        IPostProcessor postProcessor, bool enabled, CancellationToken cancellationToken = default)
    {
        await postProcessor.SetEnabledAsync(enabled, cancellationToken);
        return postProcessor.Enabled;
    }
}

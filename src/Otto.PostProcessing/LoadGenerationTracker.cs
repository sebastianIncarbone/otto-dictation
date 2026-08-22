namespace Otto.PostProcessing;

/// <summary>
/// Decides, for a background attempt that <see cref="CancelableWork"/> could not
/// truly cancel, whether it is still the one anyone is waiting on by the time its
/// blocking native call finally returns — or an orphan that a retry has since
/// superseded, whose result must be discarded (and disposed) instead of published.
///
/// <see cref="LlamaEngine"/> is a single long-lived DI singleton, and its own
/// <c>LoadAsync</c> can legitimately run more than once on the SAME instance: a
/// failed or <c>ProbeTimeout</c>-canceled load is retryable by design (see
/// <see cref="LlamaPostProcessor"/>'s <c>loadSucceeded</c> field), but the
/// orphaned worker from the abandoned attempt keeps running in the background
/// (<see cref="CancelableWork"/>'s own doc comment — it cannot be truly
/// interrupted) and can still finish AFTER a retry has already published its own
/// weights/context/executor. Without this tracker, whichever attempt's native
/// call happened to return last would win the race arbitrarily, and the loser's
/// native handles would leak.
///
/// Kept apart from <see cref="LlamaEngine"/> on purpose: it is the one piece of
/// that class with no LLamaSharp type anywhere in it — every native handle it
/// protects needs a real GGUF and a GPU to construct, but the generation
/// bookkeeping around them does not, so this is what can be exercised headlessly.
/// </summary>
public sealed class LoadGenerationTracker
{
    private readonly object gate = new();
    private long current;
    private bool disposed;

    /// <summary>
    /// Claims the next attempt id. Call once per <c>LoadAsync</c> call, BEFORE the
    /// blocking native work starts — this is what turns an older, still-running
    /// attempt into an orphan the instant a retry begins, rather than only once
    /// the retry itself finishes.
    /// </summary>
    public long ClaimGeneration()
    {
        lock (gate)
        {
            return ++current;
        }
    }

    /// <summary>
    /// Runs exactly one of <paramref name="publish"/> or <paramref name="discard"/>
    /// for the given <paramref name="attempt"/>, under the same lock
    /// <see cref="ClaimGeneration"/> and <see cref="Dispose"/> use — so the
    /// "is this attempt still current?" check can never go stale between being
    /// asked and being acted on, no matter which of two racing attempts calls
    /// this first. Returns whether <paramref name="publish"/> ran.
    /// </summary>
    public bool TryPublish(long attempt, Action publish, Action discard)
    {
        lock (gate)
        {
            if (disposed || attempt != current)
            {
                discard();
                return false;
            }

            publish();
            return true;
        }
    }

    /// <summary>
    /// Marks the tracker disposed and disposes whatever is currently published,
    /// under the same lock — so a load still in flight when this runs is
    /// guaranteed to have its own later <see cref="TryPublish"/> call refuse to
    /// publish, instead of racing this call's own disposal. Idempotent: a second
    /// call does not re-run <paramref name="disposeCurrent"/>.
    /// </summary>
    public void Dispose(Action disposeCurrent)
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            disposeCurrent();
        }
    }

    /// <summary>
    /// The reversible half of <see cref="Dispose"/> — frees whatever is
    /// currently published, under the same lock, for the idle-unload timer
    /// and the runtime correction on/off toggle, both of which need
    /// <see cref="LlamaEngine.LoadAsync"/> callable again afterward on the
    /// SAME instance, unlike <see cref="Dispose"/>'s once-per-process shutdown.
    ///
    /// Bumping <see cref="current"/> is what closes the SEPARATE race
    /// <see cref="Dispose"/> already closed a different way: a LoadAsync
    /// attempt that was still in flight when this runs (its blocking native
    /// call not truly cancellable, per <see cref="CancelableWork"/>'s own doc
    /// comment) is now older than <see cref="current"/>, so its eventual
    /// <see cref="TryPublish"/> call discards instead of resurrecting handles
    /// this method just freed — even though, unlike <see cref="Dispose"/>,
    /// nothing here is marked permanently disposed. A LATER <see cref="ClaimGeneration"/>
    /// call (a genuine reload) claims a generation past this bump and
    /// publishes normally.
    /// </summary>
    public void Unload(Action disposeCurrent)
    {
        lock (gate)
        {
            if (disposed) return;
            current++;
            disposeCurrent();
        }
    }
}

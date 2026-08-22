namespace Otto.PostProcessing;

/// <summary>
/// Fires a callback once no <see cref="Touch"/> call has landed within the
/// configured interval — what turns "unload the correction model after N idle
/// minutes" into an actual timer instead of a setting nobody reads. Kept apart
/// from <see cref="LlamaPostProcessor"/> for the same reason the other
/// concurrency primitives in this project are: no LLamaSharp type anywhere in
/// it, so the scheduling DECISION — did enough idle time really pass, does
/// reconfiguring the interval reschedule cleanly, does "never" (a null
/// interval) actually disable it — is unit-testable without a GGUF or a GPU,
/// using an injected <see cref="TimeProvider"/> instead of real wall-clock
/// minutes.
/// </summary>
public sealed class IdleUnloadScheduler : IDisposable
{
    private readonly TimeProvider clock;
    private readonly Action onIdle;
    private readonly object gate = new();

    private ITimer? timer;
    private TimeSpan? interval;

    public IdleUnloadScheduler(TimeProvider clock, Action onIdle, TimeSpan? interval)
    {
        this.clock = clock;
        this.onIdle = onIdle;
        this.interval = interval;
    }

    /// <summary>
    /// Resets the idle clock — call after every correction and after every
    /// successful load, both of which count as "not idle" for this timer's
    /// purposes. Disposes and replaces any pending timer rather than trying
    /// to reuse it: a one-shot <c>ITimer</c> created with
    /// <see cref="Timeout.InfiniteTimeSpan"/> as its period has nothing left
    /// to reschedule once it exists, so a fresh Touch always means a fresh
    /// timer. A null interval ("never") clears any pending timer instead of
    /// scheduling one, so a machine configured this way pays literally
    /// nothing for the feature.
    /// </summary>
    public void Touch()
    {
        lock (gate)
        {
            timer?.Dispose();
            timer = interval is { } due
                ? clock.CreateTimer(_ => onIdle(), null, due, Timeout.InfiniteTimeSpan)
                : null;
        }
    }

    /// <summary>
    /// Reconfigures the interval — what a Settings save or a tray change to
    /// the idle minutes calls. Reschedules from now if a timer is already
    /// pending, rather than preserving whatever fraction of the OLD interval
    /// had already elapsed: simple to reason about, and "the clock restarts
    /// when the setting changes" is an easy rule to explain if anyone ever
    /// asks why the model did not unload exactly when the old number would
    /// have predicted.
    /// </summary>
    public void Configure(TimeSpan? newInterval)
    {
        lock (gate)
        {
            interval = newInterval;

            // Reentrant: lock is Monitor-based, so this nested call is safe
            // on the same thread. Only reschedules an ALREADY pending timer —
            // an idle engine with no timer running yet stays that way until
            // the next real Touch (a load, a correction).
            if (timer is not null) Touch();
        }
    }

    /// <summary>Cancels any pending timer without scheduling a new one — what an explicit disable calls.</summary>
    public void Stop()
    {
        lock (gate)
        {
            timer?.Dispose();
            timer = null;
        }
    }

    public void Dispose() => Stop();
}

namespace Otto.PostProcessing;

/// <summary>
/// Wraps a synchronous, uncancellable blocking action so the CALLER's wait on it
/// can still be bounded by a <see cref="CancellationToken"/>.
///
/// <c>LLama.LLamaWeights.LoadFromFile</c> has no cooperative cancellation of its
/// own — it is a native call that runs to completion or failure on whatever
/// thread invokes it. Without this, <see cref="LlamaPostProcessor.ProbeAsync"/>'s
/// <c>budget.CancelAfter(options.ProbeTimeout)</c> was a lie: a hung native load
/// (a corrupt GGUF, a wedged driver) could never be cancelled, and since the
/// <see cref="SemaphoreSlim"/> gate around it is only released in the enclosing
/// <c>finally</c>, every subsequent probe or "reintentar" click would block
/// forever waiting on a load that was never going to return in time.
///
/// This does not stop the worker thread — it cannot, for the same reason the
/// budget was a lie in the first place — it only stops the caller from waiting
/// on it past the deadline. The worker keeps running to completion (or failure)
/// on its own; if it eventually succeeds, that result is simply never observed.
/// </summary>
public static class CancelableWork
{
    public static Task Run(Action work, CancellationToken cancellationToken = default)
    {
        // CancellationToken.None on the inner Task.Run: cancelling that overload
        // BEFORE it starts would abandon the work without ever running it, and
        // AFTER it starts has no effect at all on a synchronous delegate — either
        // way, tying the worker itself to the token buys nothing but confusion.
        // WaitAsync is what makes the RETURNED task observe the deadline.
        var task = Task.Run(work, CancellationToken.None);

        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    /// <summary>
    /// Generic counterpart for work that reports back a result —
    /// <see cref="LlamaEngine.UnloadAsync"/> needs to say whether it actually
    /// freed the native handles or gave up, the same bounded-caller-wait
    /// contract as <see cref="Run(Action, CancellationToken)"/>, just with
    /// something to return once the worker finishes.
    /// </summary>
    public static Task<T> Run<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        var task = Task.Run(work, CancellationToken.None);

        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }
}

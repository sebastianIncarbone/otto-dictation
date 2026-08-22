namespace Otto.PostProcessing;

/// <summary>
/// Coordinates <see cref="LlamaEngine.ChatAsync"/> calls against
/// <see cref="LlamaEngine.Dispose"/> so the native context/weights are never
/// freed while a call built on top of them — <c>ChatAsync</c> itself, and
/// <c>WarmUpAsync</c>, which drives the very same <c>ChatAsync</c> — is still
/// running.
///
/// llama.cpp's own token loop has no cooperative cancellation once inside it,
/// the same limitation <see cref="CancelableWork"/>'s own doc comment
/// describes for the blocking native load: once a call is in flight there is
/// no way to interrupt it, only to wait for it. Disposing context/weights out
/// from under it is a native access violation, not a managed exception —
/// <c>ProcessAsync</c>'s <c>catch (Exception)</c> cannot stop the process from
/// dying on it. So <see cref="LlamaEngine.Dispose"/> can only do one of two
/// safe things with this gate: wait for whatever is in flight, bounded, or
/// give up and leave the native handles for the OS to reclaim when the
/// process exits — which is the shutdown path this exists for, so a leak is
/// an acceptable outcome here and a crash is not.
///
/// Kept apart from <see cref="LlamaEngine"/> for the same reason
/// <see cref="LoadGenerationTracker"/> is: no LLamaSharp type appears
/// anywhere in it, so the synchronization DECISION is unit-testable without a
/// GGUF or a GPU, even though the calls it guards in production are not.
/// </summary>
public sealed class InFlightGate
{
    private readonly object gate = new();
    private int active;
    private bool disposed;

    // The reversible counterpart to disposed: set by TryUnload, cleared by
    // Reopen. Kept as a SEPARATE flag rather than reusing disposed itself so
    // TryDispose's own terminal guarantee never gets weakened by a Reopen
    // call reaching it by accident — see Reopen's own doc comment.
    private bool closed;

    /// <summary>
    /// Registers one in-flight call. Returns false — WITHOUT registering —
    /// once disposal has started OR the gate is currently closed by
    /// <see cref="TryUnload"/>; the caller (<see cref="LlamaEngine.ChatAsync"/>)
    /// must treat that as "the engine is gone (for now, or for good)" and
    /// throw a catchable managed exception instead of touching fields
    /// <see cref="TryDispose"/>/<see cref="TryUnload"/> may already be freeing.
    /// </summary>
    public bool TryEnter()
    {
        lock (gate)
        {
            if (disposed || closed) return false;
            active++;
            return true;
        }
    }

    /// <summary>
    /// Marks one call registered by a successful <see cref="TryEnter"/> as
    /// finished. Must run from a <c>finally</c> block — an exception mid-call
    /// must not leave a waiting <see cref="TryDispose"/> blocked forever.
    /// </summary>
    public void Exit()
    {
        lock (gate)
        {
            active--;
            Monitor.PulseAll(gate);
        }
    }

    /// <summary>
    /// Blocks every future <see cref="TryEnter"/> immediately — before this
    /// call even starts waiting — then waits up to <paramref name="timeout"/>
    /// for calls already in flight to finish before running
    /// <paramref name="disposeAction"/>. Returns false — WITHOUT running
    /// <paramref name="disposeAction"/> — if the timeout elapses first; the
    /// caller must treat that as "accept the leak, do not free the handles
    /// underneath a call still using them," never as license to free them
    /// anyway. Idempotent: once disposal has started (whether or not the
    /// first call actually got to run <paramref name="disposeAction"/>), every
    /// later call is a no-op that returns true immediately, without waiting
    /// again and without running <paramref name="disposeAction"/> a second
    /// time — there is nothing left this gate will ever wait for or free.
    /// </summary>
    public bool TryDispose(TimeSpan timeout, Action disposeAction)
    {
        lock (gate)
        {
            if (disposed) return true;
            disposed = true;

            var deadline = DateTime.UtcNow + timeout;

            while (active > 0)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(gate, remaining))
                    return false;
            }

            disposeAction();
            return true;
        }
    }

    /// <summary>
    /// The reversible half of <see cref="TryDispose"/>, for the idle-unload
    /// timer and the runtime correction on/off toggle — both need to free the
    /// model's native handles without retiring the engine for the rest of the
    /// process's life. Same coordination as <see cref="TryDispose"/> (block
    /// every future <see cref="TryEnter"/> immediately, wait up to
    /// <paramref name="timeout"/> for calls already in flight, only then run
    /// <paramref name="unloadAction"/>) and the same give-up rule (timing out
    /// means "leave it loaded," never "free it live") — but where
    /// <see cref="TryDispose"/> leaves the gate closed forever,
    /// <see cref="TryUnload"/> leaves it closed only until <see cref="Reopen"/>
    /// is called, or reopens it itself immediately if the timeout wins:
    /// nothing was actually freed in that case, so refusing new calls
    /// afterward would make the engine permanently unusable for a model that
    /// is, in fact, still fully loaded and fine to use. Idempotent the same
    /// way <see cref="TryDispose"/> is: a second call while already closed is
    /// a no-op that returns true without running <paramref name="unloadAction"/>
    /// again.
    /// </summary>
    public bool TryUnload(TimeSpan timeout, Action unloadAction)
    {
        lock (gate)
        {
            if (disposed) return true;
            if (closed) return true;

            closed = true;

            var deadline = DateTime.UtcNow + timeout;

            while (active > 0)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero || !Monitor.Wait(gate, remaining))
                {
                    closed = false;
                    return false;
                }
            }

            unloadAction();
            return true;
        }
    }

    /// <summary>
    /// Reopens a gate closed by a successful <see cref="TryUnload"/> — what a
    /// later, successful <see cref="LlamaEngine.LoadAsync"/> publish calls
    /// once new weights/executor are actually in place, so
    /// <see cref="LlamaEngine.ChatAsync"/> can reach them again. A no-op
    /// once <see cref="TryDispose"/> has run: that terminal state is never
    /// reversed by this method, on purpose — Dispose means the process is
    /// shutting down, and nothing should be able to walk that back.
    /// </summary>
    public void Reopen()
    {
        lock (gate)
        {
            if (disposed) return;
            closed = false;
        }
    }
}

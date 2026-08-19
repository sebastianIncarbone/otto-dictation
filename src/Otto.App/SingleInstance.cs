namespace Otto.App;

/// <summary>
/// Makes a second launch reach the Otto that is already running, instead of
/// starting one that competes with it.
///
/// <para>
/// Otto starts minimised to the tray, so launching it again — a double click on
/// the shortcut, the Start menu, a pinned taskbar icon — used to produce a whole
/// second process with no window of its own. It lost the race for the hotkey,
/// added a duplicate icon to the tray, and announced "no se pudo activar el
/// atajo": a warning about a conflict it had just created itself. What the
/// person wanted was the window of the Otto they already had.
/// </para>
/// <para>
/// The claim is a named mutex and the request is a named event, both under
/// <c>Local\</c> rather than <c>Global\</c>. Otto installs per user and keeps its
/// data per user, so two people signed into the same machine get one Otto each
/// rather than fighting over a single one.
/// </para>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex? claim;
    private readonly EventWaitHandle? knock;
    private readonly RegisteredWaitHandle? registration;

    /// <summary>
    /// Another launch is asking this instance to show itself.
    ///
    /// <para>
    /// Raised on a thread-pool thread, because that is where the wait completes.
    /// Anything that touches the screen has to be marshalled by the handler.
    /// </para>
    /// </summary>
    public event Action? Activated;

    private SingleInstance(Mutex? claim, string name)
    {
        this.claim = claim;

        try
        {
            knock = new EventWaitHandle(false, EventResetMode.AutoReset, KnockName(name));

            // A registered wait rather than a thread parked on WaitOne: there is
            // nothing to run between knocks, and a dedicated thread would have to
            // be woken and joined at shutdown for no gain.
            registration = ThreadPool.RegisterWaitForSingleObject(
                knock, (_, _) => Activated?.Invoke(), null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception)
        {
            // Without the event this instance simply never hears a knock, which is
            // exactly how Otto behaved before any of this existed. Degrading to
            // the old behaviour is fine; refusing to start is not.
            knock = null;
        }
    }

    /// <summary>
    /// Either the instance to hold for the lifetime of the process, or
    /// <c>null</c> when a running Otto took over and this launch should exit.
    ///
    /// <para>
    /// <c>null</c> comes back only when the running Otto was actually told to show
    /// its window. If the claim is held but nobody answers — an Otto from before
    /// this existed, a handle that would not open — this launch carries on and
    /// starts anyway. A second tray icon is a poor outcome; a shortcut that
    /// visibly does nothing at all is a worse one, and it is the one people read
    /// as "the app is broken".
    /// </para>
    /// <para>
    /// <paramref name="name"/> exists so tests can claim under their own name
    /// instead of colliding with the Otto the developer has running.
    /// </para>
    /// </summary>
    public static SingleInstance? Claim(string name = "Otto")
    {
        try
        {
            var claim = new Mutex(initiallyOwned: true, ClaimName(name), out var mine);

            if (mine) return new SingleInstance(claim, name);

            claim.Dispose();

            return Knock(name) ? null : new SingleInstance(null, name);
        }
        catch (Exception)
        {
            // Named kernel objects can be refused — a locked-down policy, an
            // exhausted handle table. Otto starts regardless.
            return new SingleInstance(null, name);
        }
    }

    /// <summary>Asks the Otto that holds the claim to show its window.</summary>
    private static bool Knock(string name)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(KnockName(name), out var knock)) return false;

            using (knock) return knock.Set();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string ClaimName(string name) => $@"Local\{name}.SingleInstance";

    private static string KnockName(string name) => $@"Local\{name}.ShowWindow";

    public void Dispose()
    {
        registration?.Unregister(null);
        knock?.Dispose();

        if (claim is null) return;

        // Released before disposing, and only from the thread that took it. A
        // mutex that is merely disposed stays abandoned until the process exits,
        // which is invisible in production — the process is exiting anyway — and
        // is exactly what a test that claims twice in a row would trip over.
        try
        {
            claim.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        claim.Dispose();
    }
}

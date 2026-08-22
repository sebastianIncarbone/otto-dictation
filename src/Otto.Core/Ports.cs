namespace Otto.Core;

/// <summary>
/// Raw audio ready for transcription: 16 kHz, mono, float32. Every capture adapter
/// converts to this so nothing downstream knows about device formats.
/// </summary>
public sealed record AudioBuffer(float[] Samples)
{
    public const int SampleRate = 16_000;

    public TimeSpan Duration => TimeSpan.FromSeconds(Samples.Length / (double)SampleRate);

    public bool IsEmpty => Samples.Length == 0;
}

/// <summary>
/// What the user was doing when they pressed the hotkey. Resolved <b>before</b>
/// transcription, not after: milestone 0.5 measured that the transcription prompt
/// has to depend on it, worth 25–50 WER points on technical dictation.
/// </summary>
public sealed record DictationContext(string ProcessName, string WindowTitle)
{
    public static readonly DictationContext Unknown = new("", "");
}

/// <summary>Global push-to-talk. Fires on press and again on release.</summary>
public interface IHotkeyService : IDisposable
{
    event Action? Pressed;
    event Action? Released;

    /// <summary>
    /// Registers <paramref name="binding"/> with the OS.
    ///
    /// <para>
    /// Obligation on the adapter: this MUST throw <see cref="HotkeyRegistrationException"/>
    /// — never swallow the failure and never leave the service silently unregistered —
    /// when the underlying registration call fails, whether because another
    /// application already holds the combination or for any other reason the OS
    /// refuses it. Before this obligation existed, a taken combination meant Otto ran
    /// with a tray icon and a window and no hotkey, with nothing telling the user why;
    /// that silence is the defect class this exception exists to close. The caller —
    /// <c>Otto.App</c>, not <see cref="Otto.Core.DictationPipeline"/>, which keeps
    /// swallowing everything else by design — is the only layer with a window to
    /// surface it through.
    /// </para>
    /// </summary>
    void Register(HotkeyBinding binding);
    void Unregister();
}

/// <summary>
/// Thrown by an <see cref="IHotkeyService.Register"/> adapter when the OS refuses the
/// registration. <see cref="AlreadyInUse"/> distinguishes the one cause the user can
/// actually act on — pick a different combination — from every other reason
/// (a reserved system combination, for instance), which is just as visible but not
/// actionable the same way.
/// </summary>
public sealed class HotkeyRegistrationException(HotkeyBinding binding, bool alreadyInUse)
    : Exception(alreadyInUse
        ? $"The combination {binding.Modifiers}+0x{binding.VirtualKey:X2} is already in use by another application."
        : $"Could not register the hotkey {binding.Modifiers}+0x{binding.VirtualKey:X2}.")
{
    public HotkeyBinding Binding { get; } = binding;

    /// <summary>True for Win32 error 1409 (ERROR_HOTKEY_ALREADY_REGISTERED).</summary>
    public bool AlreadyInUse { get; } = alreadyInUse;
}

public sealed record HotkeyBinding(HotkeyModifiers Modifiers, uint VirtualKey)
{
    public static HotkeyBinding Default => new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x20); // Ctrl+Alt+Espacio
}

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>
/// Tells whether a binding is free to register — at capture time, before the user
/// saves it — by actually attempting the registration rather than consulting a
/// hardcoded reserved-key list; Windows is the only authority on what another
/// application already holds. Two obligations on the adapter are load-bearing: it
/// MUST release whatever it took in a <c>finally</c> before returning (a leaked
/// registration would make Otto itself the app holding the combination it was
/// only supposed to be testing), and it MUST return <c>true</c> — available —
/// when it cannot tell, e.g. on a timeout (guessing "taken" on its own failure
/// would block a perfectly good binding for a reason unrelated to the binding).
/// </summary>
public interface IHotkeyAvailability
{
    bool IsAvailable(HotkeyBinding binding);
}

public interface IAudioCapture : IDisposable
{
    void Start();

    /// <summary>Stops and returns everything captured since <see cref="Start"/>.</summary>
    AudioBuffer Stop();
}

public interface ITranscriber : IAsyncDisposable
{
    /// <summary>Loads the model into memory. Slow, and done once at startup by design.</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    Task<string> TranscribeAsync(AudioBuffer audio, DictationContext context, CancellationToken cancellationToken = default);
}

public interface ITextInjector
{
    /// <summary>Types the text into whichever window currently has focus.</summary>
    Task InjectAsync(string text, CancellationToken cancellationToken = default);
}

public interface IForegroundWindow
{
    DictationContext Current();
}

/// <summary>
/// Supplies the `initial_prompt` for a context. Backed by the user's dictionary;
/// a no-op implementation is a valid starting point.
/// </summary>
public interface IPromptProvider
{
    string? PromptFor(DictationContext context);
}

/// <summary>
/// Cleans up the transcription before it reaches the user's window.
///
/// Optional by design: Otto has to work with nothing installed, so an
/// implementation that returns the text untouched is always valid, and a failure
/// here can never cost the user their dictation.
/// </summary>
public interface IPostProcessor
{
    /// <summary>True once the correction model has loaded and warmed up. Checked at startup.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Idempotent ensure-loaded, not a connectivity probe: an implementation loads
    /// (and warms up) the correction model at most once, and this is safe to call
    /// concurrently — races are serialized rather than each paying for their own
    /// load. Called once at startup — but ONLY when <see cref="Enabled"/> is
    /// already true there, see <see cref="DictationPipeline.StartAsync"/>'s own
    /// doc comment — an implementation MAY also expose it to a user-triggered
    /// retry after a failed load.
    /// </summary>
    Task<bool> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the corrected text, or the original whenever anything goes wrong.</summary>
    Task<string> ProcessAsync(string text, DictationContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the user currently wants correction on — distinct from
    /// <see cref="IsAvailable"/>, which additionally requires the model to be
    /// loaded right now. An implementation that is wired up at all (GPU
    /// hardware present) starts at whatever the setting was at launch;
    /// <see cref="SetEnabledAsync"/> is the only thing that changes it
    /// afterward. <see cref="NullPostProcessor"/> — no GPU, nothing wired —
    /// stays false forever: there is nothing here to turn on.
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// Turns correction on or off at runtime — what the Settings checkbox
    /// and the tray toggle both call, mirroring the same two-owner pattern
    /// <c>App.SetCharacterVisible</c> already uses for the character
    /// overlay. Enabling loads the model if it is not already available;
    /// disabling frees its native handles while leaving the implementation
    /// able to load again later. A no-op for an implementation with nothing
    /// to load or free — <see cref="NullPostProcessor"/>'s own version does
    /// nothing at all — and safe to call repeatedly with the same value.
    /// </summary>
    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconfigures the idle-unload interval — what a Settings save or a
    /// tray-driven change calls. Null means "never unload". A no-op for an
    /// implementation with nothing to unload.
    /// </summary>
    void SetIdleTimeout(TimeSpan? interval);

    /// <summary>
    /// True when the model was unloaded by the idle timer specifically —
    /// distinct from "never loaded" (missing GGUF, unsupported driver) and
    /// from "the user turned it off" (<see cref="Enabled"/> false). Exists so
    /// a caller (the tray) can tell "this will reload automatically on the
    /// next dictation" from "this needs a manual reintentar", which matters
    /// once <see cref="AvailabilityChanged"/> makes an idle unload visible at
    /// all — without it, that transition would read exactly like a genuine
    /// load failure. Cleared the moment a reload actually succeeds; an
    /// implementation with nothing to unload (<see cref="NullPostProcessor"/>)
    /// stays false forever.
    /// </summary>
    bool IdleUnloaded { get; }

    /// <summary>
    /// Fired whenever <see cref="IsAvailable"/>, <see cref="Enabled"/>, or
    /// <see cref="IdleUnloaded"/> actually CHANGES value as a result of
    /// <see cref="ProbeAsync"/>, <see cref="UnloadAsync"/>, or
    /// <see cref="SetEnabledAsync"/> — covers every transition after
    /// construction: an idle unload, the background reload that follows it, a
    /// manual toggle (which fires immediately, before the toggle's own load
    /// or unload has even started — see <see cref="SetEnabledAsync"/>'s own
    /// doc comment), and a manual "reintentar". Deliberately narrower than
    /// "an operation just finished": a load that fails without ever having
    /// been available does not raise this on its own (nothing observable
    /// changed) — the ONE place that also needs to know "an attempt settled,
    /// whether or not anything changed" is <c>DictationPipeline</c>'s own
    /// startup-only <c>CorrectionAvailabilityChanged</c>, which stays separate
    /// on purpose. An implementation with nothing to load or free
    /// (<see cref="NullPostProcessor"/>) never raises this.
    /// </summary>
    event Action? AvailabilityChanged;
}

/// <summary>Does nothing. What Otto uses when no local model is installed.</summary>
public sealed class NullPostProcessor : IPostProcessor
{
    public bool IsAvailable => false;

    public Task<bool> ProbeAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<string> ProcessAsync(string text, DictationContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(text);

    // Nothing is ever wired up behind this implementation — no GPU, no
    // model — so there is nothing an on/off toggle or an idle interval
    // could meaningfully change. Otto.App mirrors this at the tray/Settings
    // layer by not offering the toggle at all on hardware that resolves to
    // NullPostProcessor; these members exist so a caller that reaches them
    // anyway (a test double swapped without checking hardware first, say)
    // gets an honest "always off, does nothing" rather than a crash.
    public bool Enabled => false;

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void SetIdleTimeout(TimeSpan? interval) { }

    public bool IdleUnloaded => false;

    // No backing field, no storage — there is nothing here that could ever
    // change, so there is nothing to raise this event about. A field-backed
    // auto-event that no code path ever invokes would trip CS0067 under this
    // solution's TreatWarningsAsErrors; an explicit no-op accessor pair says
    // the same thing on purpose instead of by accident.
    public event Action? AvailabilityChanged { add { } remove { } }
}

/// <summary>
/// Turns a window into an overlay: always on top, invisible to Alt+Tab and the
/// taskbar, and transparent to clicks.
///
/// The last one is not decoration. The character floats over whatever the user is
/// working on, so every click has to reach the application underneath — and the
/// window must never take focus, because stealing it just before the text is
/// injected would send the dictation into Otto instead of into their document.
/// </summary>
public interface IOverlayStyler
{
    void MakeClickThrough(IntPtr windowHandle);
}

/// <summary>
/// A dictation, kept. Dictating and saving are the same action — there is no
/// separate "save this" step — so every dictation becomes one of these.
/// </summary>
public sealed record Note(
    long Id,
    string Title,
    string Text,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DictationContext Context,
    TimeSpan AudioDuration);

public interface INoteRepository
{
    Task<Note> AddAsync(string text, DictationContext context, TimeSpan audioDuration, CancellationToken cancellationToken = default);

    Task<Note?> GetAsync(long id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Note>> RecentAsync(int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>Full-text search over title and body.</summary>
    Task<IReadOnlyList<Note>> SearchAsync(string query, int limit = 50, CancellationToken cancellationToken = default);

    Task UpdateAsync(long id, string title, string text, CancellationToken cancellationToken = default);

    Task DeleteAsync(long id, CancellationToken cancellationToken = default);
}

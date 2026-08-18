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
    /// <summary>True once the backing service has been found. Checked at startup.</summary>
    bool IsAvailable { get; }

    Task<bool> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the corrected text, or the original whenever anything goes wrong.</summary>
    Task<string> ProcessAsync(string text, DictationContext context, CancellationToken cancellationToken = default);
}

/// <summary>Does nothing. What Otto uses when no local model is installed.</summary>
public sealed class NullPostProcessor : IPostProcessor
{
    public bool IsAvailable => false;

    public Task<bool> ProbeAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<string> ProcessAsync(string text, DictationContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(text);
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

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

    void Register(HotkeyBinding binding);
    void Unregister();
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

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Otto.Core;
using Otto.Tts;

namespace Otto.App;

/// <summary>
/// Everything the user can change. JSON in <c>%APPDATA%\Otto\</c> rather than the
/// registry: a settings file can be read, backed up, edited and deleted by the
/// person who owns it.
/// </summary>
public sealed record Settings
{
    // Derived from HotkeyBinding.Default rather than hardcoded in parallel with it: a
    // literal "Ctrl+Alt+Espacio" sitting next to Modifiers/VirtualKey is exactly the
    // shape of the bug this change closes — two sources of truth that only look
    // consistent because nobody has changed one of them yet.
    public HotkeyModifiers Modifiers { get; init; } = HotkeyBinding.Default.Modifiers;
    public uint VirtualKey { get; init; } = HotkeyBinding.Default.VirtualKey;
    public string HotkeyLabel { get; init; } = HotkeyLabels.For(HotkeyBinding.Default);

    public string Language { get; init; } = "es";
    public string Model { get; init; } = "large-v3-turbo";

    public bool StartWithWindows { get; init; }
    public bool ShowCharacter { get; init; } = true;

    /// <summary>
    /// Which overlay is shown when <see cref="ShowCharacter"/> is on. Defaults to
    /// the character, so an existing settings file with no such field keeps the
    /// overlay it already had rather than silently switching to the minimal one.
    /// </summary>
    public CharacterAppearance CharacterAppearance { get; init; } = CharacterAppearance.Character;

    /// <summary>
    /// Corrects the transcription to Rioplatense with a local model. On by default
    /// because it degrades to nothing when no model is installed, and measured at
    /// a 28% relative reduction in word error when one is.
    ///
    /// Runtime-switchable now, not just a startup decision: <c>Program.cs</c>
    /// wires <c>IPostProcessor</c> to the real corrector on any GPU machine
    /// regardless of this flag, and this only decides whether it starts
    /// loaded — see <c>DictationPipeline.StartAsync</c>'s own comment on why
    /// the two had to be decoupled.
    /// </summary>
    public bool CorrectVoseo { get; init; } = true;

    /// <summary>
    /// Minutes the correction model can sit unused before Otto unloads it to
    /// free VRAM. 0 means "never" — it stays resident for the life of the
    /// process, the feature's original always-loaded behaviour. Defaults to
    /// 15: long enough that consecutive dictations a few minutes apart never
    /// pay for a reload, short enough that leaving Otto running overnight
    /// does not hold roughly a gigabyte and a half of VRAM for nothing. Only
    /// meaningful while <see cref="CorrectVoseo"/> is on; see
    /// <c>PostProcessingOptions.IdleUnloadInterval</c> for where this turns
    /// into an actual timer.
    /// </summary>
    public int CorrectionIdleUnloadMinutes { get; init; } = 15;

    /// <summary>
    /// Off out of the box, deliberately. Otto promises to work without internet;
    /// a request at startup would turn that promise into a half-truth. The "check
    /// for updates" button is always there: a person deciding to look is not the
    /// same as the application deciding to tell them.
    /// </summary>
    public bool CheckForUpdates { get; init; }

    /// <summary>
    /// Reads the selection aloud on its own hotkey.
    ///
    /// <para>
    /// Off by default, and the asymmetry with <see cref="CorrectVoseo"/> is the point.
    /// Correction ships on because its model is already downloaded — the first run
    /// fetches it — so switching it on costs nothing the user has not already paid.
    /// A voice is not: it is a separate ~110 MB download that deliberately does not
    /// happen at first run, since hanging every install on a transfer for a feature
    /// nobody asked for is exactly what <c>ModelProvisioner</c> is not for. Turning
    /// this on is what asks for it.
    /// </para>
    /// </summary>
    public bool ReadAloud { get; init; }

    // Same three-field shape as the dictation hotkey above, including the label
    // derived from the binding rather than written beside it.
    public HotkeyModifiers ReadingModifiers { get; init; } = HotkeyBinding.DefaultReading.Modifiers;
    public uint ReadingVirtualKey { get; init; } = HotkeyBinding.DefaultReading.VirtualKey;
    public string ReadingHotkeyLabel { get; init; } = HotkeyLabels.For(HotkeyBinding.DefaultReading);

    /// <summary>
    /// Which Piper voice reads. Defaults to the catalogue's own default rather than a
    /// literal id, for the reason the hotkey label already documents: two sources of
    /// truth that agree only until somebody changes one of them.
    ///
    /// <para>
    /// An id this build does not know falls back to the Argentine default rather than
    /// throwing — see <c>Voices.Resolve</c>. That matters here because this value is in
    /// a file the user can edit and an older Otto may have written.
    /// </para>
    /// </summary>
    public string ReadingVoice { get; init; } = Voices.Default.Id;

    /// <summary>
    /// How that voice is sampled — what survived of the "effort level" idea. Not a
    /// choice of model: the only Argentine voice exists at one quality tier, so a
    /// lighter model would cost the accent. See <c>PiperVoicing</c>.
    /// </summary>
    public string ReadingVoicing { get; init; } = PiperVoicing.Natural.Id;

    /// <summary>
    /// Playback speed for a reading, as an id rather than a number — same shape and same
    /// reason as the two fields above.
    ///
    /// <para>
    /// Stored at all because the control that moves it floats over the reading rather than
    /// living in Ajustes: somebody who reads everything at x1,5 would otherwise re-choose
    /// it on every single reading. Written by <c>App</c> when the card changes it, which
    /// is why it is one more field that <c>MainViewModel.ApplyTo</c> must not clobber —
    /// see the amend-never-rebuild rule.
    /// </para>
    /// </summary>
    public string ReadingSpeed { get; init; } = Otto.Core.ReadingSpeed.Normal.Id;

    /// <summary>True when no settings file existed yet, so the window can introduce itself.</summary>
    [JsonIgnore] public bool IsFirstRun { get; init; }

    public HotkeyBinding ToBinding() => new(Modifiers, VirtualKey);

    public HotkeyBinding ToReadingBinding() => new(ReadingModifiers, ReadingVirtualKey);
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string path;

    public SettingsStore(string path) => this.path = path;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Otto", "config.json");

    public Settings Load()
    {
        if (!File.Exists(path)) return new Settings { IsFirstRun = true };

        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Options) ?? new Settings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt or unreadable settings file must not stop the app from
            // starting — the user would have no way to fix it from inside Otto.
            return new Settings();
        }
    }

    public void Save(Settings settings)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
    }
}

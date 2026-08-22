using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Otto.Core;

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

    /// <summary>True when no settings file existed yet, so the window can introduce itself.</summary>
    [JsonIgnore] public bool IsFirstRun { get; init; }

    public HotkeyBinding ToBinding() => new(Modifiers, VirtualKey);
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

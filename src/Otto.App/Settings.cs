using System.Text.Encodings.Web;
using System.Text.Json;
using Otto.Core;

namespace Otto.App;

/// <summary>
/// Everything the user can change. JSON in <c>%APPDATA%\Otto\</c> rather than the
/// registry: a settings file can be read, backed up, edited and deleted by the
/// person who owns it.
/// </summary>
public sealed record Settings
{
    public HotkeyModifiers Modifiers { get; init; } = HotkeyModifiers.Control | HotkeyModifiers.Alt;
    public uint VirtualKey { get; init; } = 0x20;               // Espacio
    public string HotkeyLabel { get; init; } = "Ctrl+Alt+Espacio";

    public string Language { get; init; } = "es";
    public string Model { get; init; } = "large-v3-turbo";

    public bool StartWithWindows { get; init; }
    public bool ShowCharacter { get; init; } = true;

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
        if (!File.Exists(path)) return new Settings();

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

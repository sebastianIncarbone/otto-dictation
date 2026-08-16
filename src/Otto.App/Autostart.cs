using Microsoft.Win32;

namespace Otto.App;

/// <summary>
/// Starting with Windows, without an installer.
///
/// A portable ZIP has nobody to register it, so the app has to do it. The Run key
/// under HKEY_CURRENT_USER needs no elevation and is visible to the user in Task
/// Manager's Startup tab, which matters for something that runs in the background.
/// </summary>
public static class Autostart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Otto";

    public static void Apply(bool enabled)
    {
        using var run = Registry.CurrentUser.OpenSubKey(Key, writable: true);
        if (run is null) return;

        if (!enabled)
        {
            run.DeleteValue(Name, throwOnMissingValue: false);
            return;
        }

        if (ExecutablePath() is { } path)
            run.SetValue(Name, $"\"{path}\"");
    }

    public static bool IsEnabled()
    {
        using var run = Registry.CurrentUser.OpenSubKey(Key);
        return run?.GetValue(Name) is not null;
    }

    /// <summary>
    /// Re-points the entry if the folder moved. A portable app gets dragged around,
    /// and a stale Run entry fails silently at boot — the user just finds Otto is
    /// no longer starting and has no idea why.
    /// </summary>
    public static void RepairIfMoved()
    {
        if (!IsEnabled() || ExecutablePath() is not { } path) return;

        using var run = Registry.CurrentUser.OpenSubKey(Key, writable: true);

        if (run?.GetValue(Name) as string != $"\"{path}\"")
            run?.SetValue(Name, $"\"{path}\"");
    }

    private static string? ExecutablePath() =>
        Environment.ProcessPath is { } path && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? path
            : null;
}

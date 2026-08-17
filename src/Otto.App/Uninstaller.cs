using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Otto.App;

/// <summary>
/// Removes everything Otto put outside its own folder.
///
/// A portable ZIP has no uninstaller, so deleting the folder is the obvious move —
/// and it leaves the settings, the models and every note the user ever dictated
/// scattered in AppData, invisible and forever. An application that cannot be
/// removed cleanly is asking to be resented.
///
/// The executable's own folder is deliberately not touched: a program deleting
/// itself while running is a mess, and it is the one part the user can obviously
/// throw in the bin themselves.
///
/// <para>
/// Otto now ships two ways, so there are two ways to remove it, and they must not
/// contradict each other. When it was installed, Windows owns the removal and this
/// class only points at it — doing the delete here would wipe the data while
/// leaving the program files and the "Agregar o quitar programas" entry behind,
/// which is a worse mess than the one it was written to prevent. When it was
/// unzipped, nothing else is going to do the job, so this class does it.
/// </para>
/// </summary>
public static class Uninstaller
{
    /// <summary>
    /// Matches the AppId in <c>build/otto.iss</c>. Inno Setup appends <c>_is1</c>
    /// and, because Otto installs per user and never asks for administrator, the
    /// entry lives under HKCU rather than HKLM.
    /// </summary>
    private const string UninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{08FD4B32-9406-4142-A528-1E908B2A4A09}_is1";

    /// <summary>
    /// The installer's own uninstaller, or null when this is the portable copy.
    /// Presence of the key is what tells the two distributions apart at runtime.
    /// </summary>
    public static string? InstalledUninstaller()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);

        var command = key?.GetValue("UninstallString") as string;

        return string.IsNullOrWhiteSpace(command) ? null : command;
    }

    /// <summary>
    /// Hands the job to Windows. Returns false if the uninstaller could not be
    /// started, so the caller can stay open instead of shutting down into nothing.
    /// </summary>
    public static bool LaunchInstalled(string command, ILogger logger)
    {
        try
        {
            // Inno stores the path quoted. ProcessStartInfo wants it bare, and a
            // stray pair of quotes here reads as "file not found".
            var executable = command.Trim().Trim('"');

            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo abrir el desinstalador de Windows");
            return false;
        }
    }

    public static IReadOnlyList<string> Locations() =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Otto"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Otto"),
    ];

    /// <summary>What is about to be deleted, so the confirmation can be specific.</summary>
    public static (long Bytes, int Notes) Summarise(string databasePath)
    {
        var bytes = Locations()
            .Where(Directory.Exists)
            .Sum(dir => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(file => new FileInfo(file).Length));

        return (bytes, CountNotes(databasePath));
    }

    private static int CountNotes(string databasePath)
    {
        if (!File.Exists(databasePath)) return 0;

        try
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");

            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM notes";

            return Convert.ToInt32(command.ExecuteScalar());
        }
        catch
        {
            // Only used to make the warning concrete. Not worth failing over.
            return 0;
        }
    }

    public static void Run(ILogger logger)
    {
        Autostart.Apply(enabled: false);

        foreach (var location in Locations().Where(Directory.Exists))
        {
            try
            {
                Directory.Delete(location, recursive: true);
                logger.LogInformation("Borrado {Location}", location);
            }
            catch (Exception ex)
            {
                // Reported rather than swallowed: the user asked for this to be
                // gone and deserves to know if part of it survived.
                logger.LogError(ex, "No se pudo borrar {Location}", location);
            }
        }
    }
}

using Microsoft.Extensions.Logging;

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
/// </summary>
public static class Uninstaller
{
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

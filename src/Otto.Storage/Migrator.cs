using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Otto.Storage;

/// <summary>
/// Applies the numbered SQL scripts under <c>Migrations/</c> in order, once each.
///
/// Deliberately not an ORM's migration engine: the schema is two tables, and a
/// reader of this repository can see exactly what the database looks like by
/// opening one .sql file. If the schema ever grows enough to make that painful,
/// that is the signal to reach for EF Core.
/// </summary>
public static class Migrator
{
    public static void Run(SqliteConnection connection, ILogger logger)
    {
        EnsureVersionTable(connection);

        var applied = AppliedVersions(connection);

        foreach (var (name, sql) in EmbeddedScripts())
        {
            if (applied.Contains(name)) continue;

            // One transaction per script: a failure halfway leaves the database on
            // the previous version rather than in a shape no code expects.
            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }

            using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = "INSERT INTO schema_version (name, applied_at) VALUES ($name, $at)";
                record.Parameters.AddWithValue("$name", name);
                record.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                record.ExecuteNonQuery();
            }

            transaction.Commit();
            logger.LogInformation("Migration applied: {Name}", name);
        }
    }

    private static void EnsureVersionTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS schema_version (
                name       TEXT PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private static HashSet<string> AppliedVersions(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM schema_version";

        using var reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.Ordinal);

        while (reader.Read()) names.Add(reader.GetString(0));

        return names;
    }

    /// <summary>Ordered by name, which is why the files are numbered.</summary>
    private static IEnumerable<(string Name, string Sql)> EmbeddedScripts()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var names = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal);

        foreach (var resource in names)
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);

            yield return (ShortName(resource), reader.ReadToEnd());
        }
    }

    private static string ShortName(string resource)
    {
        var parts = resource.Split('.');
        return parts.Length >= 2 ? $"{parts[^2]}.{parts[^1]}" : resource;
    }
}

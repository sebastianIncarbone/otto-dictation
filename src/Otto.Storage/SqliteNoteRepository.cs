using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Otto.Core;

namespace Otto.Storage;

/// <summary>
/// Notes on disk, in one SQLite file.
///
/// Chosen over a JSON file because search has to stay instant with thousands of
/// notes — FTS5 does that without loading everything into memory — and because
/// writes are transactional, so closing the app badly cannot corrupt the history.
/// It also keeps the product's promise honest: everything lives on this machine,
/// in one file the user can see and copy.
/// </summary>
public sealed class SqliteNoteRepository : INoteRepository, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly SemaphoreSlim gate = new(1, 1);

    public SqliteNoteRepository(string databasePath, ILogger<SqliteNoteRepository> logger)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,

            // One connection is held for the life of the process, so pooling buys
            // nothing and costs predictability: a pooled connection keeps the file
            // handle open after Dispose, which leaves the database locked for
            // anything that wants to move, back up or delete it.
            Pooling = false,
        }.ToString());

        connection.Open();

        // WAL keeps a read (the notes list) from blocking a write (a dictation
        // landing) and survives an abrupt shutdown better than the default journal.
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA foreign_keys=ON;");

        Migrator.Run(connection, logger);
    }

    private void Execute(string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public async Task<Note> AddAsync(
        string text,
        DictationContext context,
        TimeSpan audioDuration,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        await gate.WaitAsync(cancellationToken);

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO notes (title, text, created_at, updated_at, process_name, window_title, audio_seconds)
                VALUES ('', $text, $now, $now, $process, $window, $seconds)
                RETURNING id;
                """;

            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$process", context.ProcessName);
            command.Parameters.AddWithValue("$window", context.WindowTitle);
            command.Parameters.AddWithValue("$seconds", audioDuration.TotalSeconds);

            var id = (long)(await command.ExecuteScalarAsync(cancellationToken))!;

            return new Note(id, "", text, now, now, context, audioDuration);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<Note?> GetAsync(long id, CancellationToken cancellationToken = default) =>
        QuerySingleAsync($"{SelectColumns} WHERE id = $id", cancellationToken,
            ("$id", id));

    public Task<IReadOnlyList<Note>> RecentAsync(int limit = 50, CancellationToken cancellationToken = default) =>
        QueryAsync($"{SelectColumns} ORDER BY created_at DESC LIMIT $limit", cancellationToken,
            ("$limit", limit));

    public Task<IReadOnlyList<Note>> SearchAsync(
        string query,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return RecentAsync(limit, cancellationToken);

        // rank orders by FTS5 relevance; joining back to notes keeps one source of
        // truth for the columns rather than storing them twice.
        return QueryAsync(
            """
            SELECT n.id, n.title, n.text, n.created_at, n.updated_at, n.process_name, n.window_title, n.audio_seconds
            FROM notes_fts f
            JOIN notes n ON n.id = f.rowid
            WHERE notes_fts MATCH $query
            ORDER BY f.rank
            LIMIT $limit;
            """,
            cancellationToken,
            ("$query", ToMatchExpression(query)),
            ("$limit", limit));
    }

    /// <summary>
    /// User input is not FTS5 syntax. Quoting each term and appending * gives
    /// prefix matching without letting a stray quote or operator throw a syntax
    /// error at someone who just typed a word with an apostrophe.
    /// </summary>
    private static string ToMatchExpression(string query)
    {
        var terms = query
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => $"\"{term.Replace("\"", "\"\"")}\"*");

        return string.Join(' ', terms);
    }

    public async Task UpdateAsync(long id, string title, string text, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE notes SET title = $title, text = $text, updated_at = $now WHERE id = $id";

            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM notes WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private const string SelectColumns =
        "SELECT id, title, text, created_at, updated_at, process_name, window_title, audio_seconds FROM notes";

    private async Task<Note?> QuerySingleAsync(
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        var rows = await QueryAsync(sql, cancellationToken, parameters);
        return rows.Count > 0 ? rows[0] : null;
    }

    private async Task<IReadOnlyList<Note>> QueryAsync(
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;

            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var notes = new List<Note>();

            while (await reader.ReadAsync(cancellationToken))
                notes.Add(Read(reader));

            return notes;
        }
        finally
        {
            gate.Release();
        }
    }

    private static Note Read(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        DateTimeOffset.Parse(reader.GetString(3)),
        DateTimeOffset.Parse(reader.GetString(4)),
        new DictationContext(reader.GetString(5), reader.GetString(6)),
        TimeSpan.FromSeconds(reader.GetDouble(7)));

    public void Dispose()
    {
        connection.Dispose();
        gate.Dispose();
    }
}

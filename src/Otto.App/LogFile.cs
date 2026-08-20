using System.Text;
using Microsoft.Extensions.Logging;

namespace Otto.App;

/// <summary>
/// Otto's only durable record of what it did.
///
/// <para>
/// Otto is a <c>WinExe</c>: no console is attached when it runs the way people
/// actually run it, so <c>AddSimpleConsole</c> has been writing into nothing.
/// Until this existed a crash left no trace whatsoever — the tray icon
/// disappeared and the only evidence was the user noticing. Diagnosing that
/// meant reproducing it by hand.
/// </para>
/// <para>
/// The file is opened and closed per line rather than held open, and that is
/// deliberate. <see cref="Uninstaller"/> deletes <c>%LOCALAPPDATA%\Otto</c>
/// recursively while Otto is still running; a <see cref="StreamWriter"/> parked
/// on the log would keep the directory locked and turn an uninstall into a
/// half-finished one. Otto logs a handful of lines per dictation, so the cost
/// of reopening is irrelevant next to that.
/// </para>
/// </summary>
public sealed class LogFile
{
    /// <summary>
    /// Rotate at a megabyte and keep exactly one previous file. Otto runs for
    /// weeks at a time, so the log cannot be allowed to grow without bound; but
    /// a crash has to still be readable after the restart that follows it, and
    /// a single truncating file would lose it.
    /// </summary>
    private const long MaxBytes = 1024 * 1024;

    /// <summary>
    /// Every adapter logs, and they do not share a thread: the pipeline runs on
    /// the thread pool, the hotkey on a Win32 message loop, the UI on the
    /// dispatcher. Appends are serialised here because nothing else serialises
    /// them.
    /// </summary>
    private readonly Lock gate = new();

    private readonly string path;

    public LogFile(string path)
    {
        this.path = path;

        // Guarded like every other write: a log that throws on construction
        // would take down the app it exists to explain.
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is not null) Directory.CreateDirectory(directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing to report it to. Appends will fail the same way and are
            // swallowed there too.
        }
    }

    /// <summary>Where the file is, for anyone who has to go and read it.</summary>
    public string FullPath => path;

    public void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var line = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(' ')
            .Append(Label(level))
            .Append(' ')
            .Append(category)
            .Append(": ")
            .Append(message);

        // The whole exception, not just its message: the inner exception and the
        // stack are the part worth having, and an HRESULT alone explains nothing.
        if (exception is not null) line.AppendLine().Append(exception);

        lock (gate)
        {
            // Rotating and appending are guarded separately on purpose. Sharing a
            // single try block meant a failed rotation jumped over the append and
            // took the line with it — and past the size threshold *every* write
            // attempts a rotation, so anything holding a handle on the log (an
            // editor left open on it, a scanner, a backup) silently ate the
            // dictation log and, worse, the crash line it was reporting. That is
            // the exact no-trace failure this file exists to end.
            try
            {
                Rotate();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The file stays oversized until some later write manages to move
                // it. An over-long log costs disk; a dropped line costs the crash.
            }

            try
            {
                File.AppendAllText(path, line.AppendLine().ToString(), Encoding.UTF8);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A logger that throws is worse than a logger that misses a line,
                // and this one is called from inside the crash handlers — throwing
                // there would replace the crash being reported with a new one.
            }
        }
    }

    private void Rotate()
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxBytes) return;

        File.Move(path, path + ".1", overwrite: true);
    }

    /// <summary>
    /// The same four-letter labels <c>AddSimpleConsole</c> prints, so a line read
    /// from the file and a line read from a console-attached run look alike.
    /// </summary>
    private static string Label(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "none",
    };
}

/// <summary>
/// Bridges <see cref="LogFile"/> into <c>Microsoft.Extensions.Logging</c>, so
/// every adapter that already takes an <see cref="ILogger{T}"/> lands in the file
/// without knowing the file exists.
/// </summary>
public sealed class FileLoggerProvider(LogFile file) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(file, categoryName);

    // Nothing is held open between lines, so there is nothing to release.
    public void Dispose() { }

    private sealed class FileLogger(LogFile file, string category) : ILogger
    {
        // Scopes are not written: nothing in Otto opens one.
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            file.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}

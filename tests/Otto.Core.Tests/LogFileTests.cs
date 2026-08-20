using Microsoft.Extensions.Logging;
using Otto.App;

namespace Otto.Core.Tests;

/// <summary>
/// Against a real file, not a substitute. The whole point of this class is what
/// ends up on disk after Otto is gone, and a mock would assert nothing about that.
/// </summary>
public class LogFileTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), $"otto-test-{Guid.NewGuid():N}");

    private readonly string path;

    public LogFileTests() => path = Path.Combine(directory, "logs", "otto.log");

    [Fact]
    public void Crea_el_directorio_y_escribe_la_linea()
    {
        new LogFile(path).Write(LogLevel.Information, "Otto.Test", "hola", null);

        var written = File.ReadAllText(path);

        Assert.Contains("info", written);
        Assert.Contains("Otto.Test", written);
        Assert.Contains("hola", written);
    }

    [Fact]
    public void Agrega_en_vez_de_pisar()
    {
        var log = new LogFile(path);

        log.Write(LogLevel.Information, "Otto.Test", "primera", null);
        log.Write(LogLevel.Information, "Otto.Test", "segunda", null);

        var lines = File.ReadAllLines(path);

        Assert.Equal(2, lines.Length);
        Assert.Contains("primera", lines[0]);
        Assert.Contains("segunda", lines[1]);
    }

    [Fact]
    public void Escribe_la_excepcion_entera_y_no_solo_el_mensaje()
    {
        // The message alone is the part that explains least. An HRESULT with no
        // stack behind it is exactly the log line that wastes an afternoon.
        var exception = new InvalidOperationException("se rompio", new IOException("la causa"));

        new LogFile(path).Write(LogLevel.Critical, "Otto.Test", "cayo", exception);

        var written = File.ReadAllText(path);

        Assert.Contains("crit", written);
        Assert.Contains("InvalidOperationException", written);
        Assert.Contains("la causa", written);
    }

    [Fact]
    public void Rota_al_pasarse_de_tamano_y_conserva_lo_anterior()
    {
        // A crash has to still be readable after the restart that follows it, so
        // rotating cannot mean truncating.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new string('x', 1024 * 1024 + 1));

        new LogFile(path).Write(LogLevel.Information, "Otto.Test", "despues de rotar", null);

        Assert.Contains("despues de rotar", File.ReadAllText(path));
        Assert.True(File.Exists(path + ".1"));
        Assert.StartsWith("xxx", File.ReadAllText(path + ".1"));
    }

    [Fact]
    public void Una_rotacion_que_falla_no_se_lleva_la_linea()
    {
        // Past the threshold every write attempts a rotation, so a handle held on
        // the log — an editor left open on it, a scanner, a backup — used to eat
        // the line instead of just the rotation. The line is the whole point:
        // this is the path a crash report travels through.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new string('x', 1024 * 1024 + 1));

        // Sharing read and write but NOT delete: appending still works, moving
        // cannot, which is exactly the situation a reader of the log creates.
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // Proves the rotation really is failing rather than the test passing for
        // the wrong reason.
        Assert.ThrowsAny<IOException>(() => File.Move(path, path + ".1", overwrite: true));

        new LogFile(path).Write(LogLevel.Critical, "Otto.Test", "el proceso se termina", null);

        Assert.Contains("el proceso se termina", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".1"));
    }

    [Fact]
    public void No_rota_por_debajo_del_limite()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "anterior" + Environment.NewLine);

        new LogFile(path).Write(LogLevel.Information, "Otto.Test", "nueva", null);

        Assert.False(File.Exists(path + ".1"));
        Assert.Equal(2, File.ReadAllLines(path).Length);
    }

    [Fact]
    public void Un_destino_imposible_no_tira_la_aplicacion()
    {
        // The logger is called from inside the crash handlers. Throwing there
        // would replace the crash being reported with a brand new one.
        // Impossible for a concrete reason: the parent of the directory Otto would
        // have to create is a file, so creating it cannot succeed.
        Directory.CreateDirectory(directory);
        var blocker = Path.Combine(directory, "bloqueado");
        File.WriteAllText(blocker, "soy un archivo, no un directorio");

        var impossible = new LogFile(Path.Combine(blocker, "logs", "otto.log"));

        // Proves the guard is doing work rather than the destination being fine:
        // the same write without it throws.
        Assert.ThrowsAny<IOException>(
            () => File.AppendAllText(Path.Combine(blocker, "logs", "otto.log"), "x"));

        var exception = Record.Exception(
            () => impossible.Write(LogLevel.Critical, "Otto.Test", "cayo", null));

        Assert.Null(exception);
    }

    [Fact]
    public void El_proveedor_lleva_a_los_ILogger_de_siempre_hasta_el_archivo()
    {
        // The adapters take ILogger<T> and know nothing about a file. This is the
        // bridge Program.cs installs, and it is the part that decides whether any
        // of the logging already in the codebase reaches disk at all.
        using var factory = LoggerFactory.Create(builder => builder
            .AddProvider(new FileLoggerProvider(new LogFile(path)))
            .SetMinimumLevel(LogLevel.Information));

        factory.CreateLogger<LogFileTests>().LogWarning("algo raro paso");

        var written = File.ReadAllText(path);

        Assert.Contains("warn", written);
        Assert.Contains(nameof(LogFileTests), written);
        Assert.Contains("algo raro paso", written);
    }

    [Fact]
    public void El_proveedor_respeta_el_nivel_minimo()
    {
        using var factory = LoggerFactory.Create(builder => builder
            .AddProvider(new FileLoggerProvider(new LogFile(path)))
            .SetMinimumLevel(LogLevel.Information));

        factory.CreateLogger<LogFileTests>().LogDebug("ruido");

        Assert.False(File.Exists(path));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

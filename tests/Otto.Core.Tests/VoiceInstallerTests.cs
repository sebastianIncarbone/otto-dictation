using Microsoft.Extensions.Logging.Abstractions;
using Otto.Tts;

namespace Otto.Core.Tests;

/// <summary>
/// Bajar una voz. Sin red: la fuente está detrás de un puerto justamente para que el
/// orden de los archivos y qué cuenta como terminado se puedan ejercitar en memoria.
/// </summary>
public class VoiceInstallerTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "otto-instalador-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);

        GC.SuppressFinalize(this);
    }

    private VoiceInstaller Installer(IVoiceSource source) =>
        new(new TtsOptions { EngineDirectory = directory, VoicesDirectory = directory },
            source,
            NullLogger<VoiceInstaller>.Instance);

    [Fact]
    public async Task Baja_el_config_antes_que_el_modelo()
    {
        // El config pesa kilobytes contra los cien y pico megas del modelo, así que una
        // URL equivocada o un portal cautivo se descubren en un milisegundo en vez de
        // después de una descarga larga que nunca iba a servir.
        var source = new FakeSource(directory);

        await Installer(source).InstallAsync(Voices.Default);

        Assert.Equal(2, source.Fetched.Count);
        Assert.EndsWith(".onnx.json", source.Fetched[0], StringComparison.Ordinal);
        Assert.EndsWith(".onnx", source.Fetched[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_baja_nada_si_la_voz_ya_esta_en_el_disco()
    {
        // Idempotente a propósito: esto se llama en cada guardado de Ajustes.
        Directory.CreateDirectory(directory);
        File.WriteAllText(Voices.Default.ModelPath(directory), "");
        File.WriteAllText(Voices.Default.ConfigPath(directory), "");

        var source = new FakeSource(directory);

        await Installer(source).InstallAsync(Voices.Default);

        Assert.Empty(source.Fetched);
    }

    [Fact]
    public async Task Vuelve_a_bajar_si_falta_una_de_las_dos_mitades()
    {
        // Una descarga cortada deja media voz. Si esto la diera por instalada, pasaría
        // todos los chequeos de Ajustes y recién fallaría en el primer fragmento.
        Directory.CreateDirectory(directory);
        File.WriteAllText(Voices.Default.ConfigPath(directory), "");

        var source = new FakeSource(directory);

        await Installer(source).InstallAsync(Voices.Default);

        Assert.Equal(2, source.Fetched.Count);
    }

    [Fact]
    public async Task Falla_si_la_fuente_dice_que_termino_y_no_dejo_los_archivos()
    {
        // La post-condición que el resto del código confía antes de prender la lectura.
        var source = new FakeSource(directory) { LeaveFiles = false };

        await Assert.ThrowsAsync<IOException>(() => Installer(source).InstallAsync(Voices.Default));
    }

    [Fact]
    public async Task Deja_pasar_la_cancelacion_del_usuario()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var source = new FakeSource(directory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Installer(source).InstallAsync(Voices.Default, null, cancellation.Token));
    }

    /// <summary>
    /// Una fuente que cumple el contrato del puerto: deja los dos archivos en su lugar
    /// final. <see cref="LeaveFiles"/> la hace mentir, que es el caso que el instalador
    /// tiene que atrapar.
    /// </summary>
    private sealed class FakeSource(string directory) : IVoiceSource
    {
        public List<string> Fetched { get; } = [];

        public bool LeaveFiles { get; init; } = true;

        public Task FetchAsync(string url, string destination,
            IProgress<VoiceDownloadProgress>? progress, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Fetched.Add(url);

            if (LeaveFiles)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(destination, "");
            }

            progress?.Report(new VoiceDownloadProgress(1, 1, 0));

            return Task.CompletedTask;
        }
    }
}

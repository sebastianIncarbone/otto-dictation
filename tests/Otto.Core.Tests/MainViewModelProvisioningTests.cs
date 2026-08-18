using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Otto.App;
using Otto.App.ViewModels;
using Otto.Core;
using Otto.Speech;

namespace Otto.Core.Tests;

/// <summary>
/// MainViewModel.Apply(ProvisioningStatus) is pure branch-and-string projection
/// with no UI dependency. Compiled bindings (AvaloniaUseCompiledBindingsByDefault
/// + x:DataType) check every binding *path* it feeds, but nothing about its
/// *content* — the "{p:P0} · {mb:N1} MB/s" formatting, the "Progress is null"
/// reassurance branch, and the fact that an unrecognised state must degrade to
/// "not provisioning" instead of latching the card open. That content is what
/// these tests pin down; the Avalonia/XAML carve-out still applies to the view
/// itself.
/// </summary>
public class MainViewModelProvisioningTests
{
    private static MainViewModel Build()
    {
        var availability = Substitute.For<IHotkeyAvailability>();
        availability.IsAvailable(Arg.Any<HotkeyBinding>()).Returns(true);

        return new(
            Substitute.For<INoteRepository>(),
            new DictationPipeline(
                Substitute.For<IHotkeyService>(),
                Substitute.For<IAudioCapture>(),
                Substitute.For<ITranscriber>(),
                Substitute.For<ITextInjector>(),
                Substitute.For<IForegroundWindow>(),
                Substitute.For<INoteRepository>(),
                new NullPostProcessor(),
                NullLogger<DictationPipeline>.Instance),
            new SettingsStore(Path.Combine(Path.GetTempPath(), "otto-tests-no-escribe.json")),
            new Settings(),
            databasePath: "",
            clipboard: () => null,
            provisioningOptions: new ProvisioningOptions
            {
                ModelsDirectory = "",
                SpeechFileName = "",
                VadFileName = "",
                Label = "large-v3-turbo",
                Size = "~1,6 GB",
            },
            availability);
    }

    [Fact]
    public void Descargando_el_habla_sin_progreso_muestra_el_mensaje_de_tranquilidad()
    {
        var view = Build();

        view.Apply(new ProvisioningStatus(ProvisioningState.DownloadingSpeech));

        Assert.True(view.IsProvisioning);
        Assert.False(view.HasProvisioningError);
        Assert.Contains("large-v3-turbo", view.ProvisioningText);
        Assert.Contains("~1,6 GB", view.ProvisioningText);
        Assert.Equal("Si se corta, la próxima vez continúa desde donde quedó.", view.ProvisioningDetail);
        Assert.Equal(0, view.ProvisioningPercent);
    }

    [Fact]
    public void Descargando_el_habla_con_progreso_formatea_porcentaje_y_velocidad()
    {
        var view = Build();

        view.Apply(new ProvisioningStatus(
            ProvisioningState.DownloadingSpeech,
            new DownloadProgress(50, 100, 2 * 1024 * 1024)));

        Assert.Equal($"{0.5:P0} · {2.0:N1} MB/s", view.ProvisioningDetail);
        Assert.Equal(0.5, view.ProvisioningPercent);
    }

    [Fact]
    public void Preparando_el_detector_de_voz_resetea_detalle_y_porcentaje()
    {
        var view = Build();

        view.Apply(new ProvisioningStatus(ProvisioningState.PreparingVad));

        Assert.True(view.IsProvisioning);
        Assert.Equal("Preparando el detector de voz…", view.ProvisioningText);
        Assert.Equal("", view.ProvisioningDetail);
        Assert.Equal(0, view.ProvisioningPercent);
    }

    [Fact]
    public void Una_falla_muestra_el_error_y_no_toca_el_texto_de_la_descarga_anterior()
    {
        var view = Build();

        view.Apply(new ProvisioningStatus(ProvisioningState.DownloadingSpeech));
        var textoAntesDeLaFalla = view.ProvisioningText;

        view.Apply(new ProvisioningStatus(ProvisioningState.Failed));

        Assert.True(view.IsProvisioning);
        Assert.True(view.HasProvisioningError);
        Assert.Equal(
            "No se pudo descargar el modelo. Fijate que tengas internet y probá de nuevo — lo que ya se bajó no se pierde.",
            view.ProvisioningError);

        // Failed has its own case now and never falls into the download/VAD
        // branches, so it deliberately leaves the prior text/detail/percent alone.
        Assert.Equal(textoAntesDeLaFalla, view.ProvisioningText);
    }

    [Fact]
    public void Listo_apaga_IsProvisioning_y_el_error()
    {
        var view = Build();

        view.Apply(new ProvisioningStatus(ProvisioningState.Failed));
        view.Apply(new ProvisioningStatus(ProvisioningState.Ready));

        Assert.False(view.IsProvisioning);
        Assert.False(view.HasProvisioningError);
    }

    [Fact]
    public void Un_estado_no_reconocido_no_deja_la_tarjeta_trabada()
    {
        // Idle is never reported through this channel by ModelProvisioner today,
        // but the whole point of the explicit switch is that it wouldn't matter
        // if it were: this must degrade to "not provisioning", not latch the card
        // open with no error and no Reintentar.
        var view = Build();

        view.Apply(new ProvisioningStatus(ProvisioningState.DownloadingSpeech));
        view.Apply(new ProvisioningStatus(ProvisioningState.Idle));

        Assert.False(view.IsProvisioning);
        Assert.False(view.HasProvisioningError);
    }
}

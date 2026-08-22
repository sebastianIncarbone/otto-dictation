using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Otto.App;
using Otto.App.ViewModels;
using Otto.Core;
using Otto.Speech;

namespace Otto.Core.Tests;

/// <summary>
/// <see cref="MainViewModel.CorrectVoseoChangedSinceApplied"/> — the guard that
/// stops <see cref="MainViewModel.SaveSettings"/> from retrying a ~2 GB
/// correction-model download on every unrelated settings save.
///
/// <para>
/// Deliberately never invokes <c>SaveSettingsCommand</c> itself: that command
/// also calls <c>Autostart.Apply</c>, which writes to the real
/// <c>HKEY_CURRENT_USER\...\Run</c> registry key — not something a headless
/// unit test may touch, on this machine or any other. <see cref="MainViewModel.CorrectVoseoChangedSinceApplied"/>
/// exists specifically so this decision is checkable without going anywhere
/// near that, the same reason <see cref="MainViewModel.ApplyTo"/> is public
/// and separate from the save itself.
/// </para>
/// </summary>
public class MainViewModelCorrectVoseoTests
{
    private static MainViewModel Build(Settings? stored = null)
    {
        var availability = Substitute.For<IHotkeyAvailability>();
        availability.IsAvailable(Arg.Any<HotkeyBinding>()).Returns(true);

        return new(
            Substitute.For<INoteRepository>(),
            BuildPipeline(),
            new SettingsStore(Path.Combine(Path.GetTempPath(), "otto-tests-no-escribe.json")),
            stored ?? new Settings(),
            databasePath: "",
            clipboard: () => null,
            provisioningOptions: new ProvisioningOptions
            {
                ModelsDirectory = "", SpeechFileName = "", VadFileName = "", Label = "", Size = "",
            },
            availability);
    }

    private static DictationPipeline BuildPipeline() => new(
        Substitute.For<IHotkeyService>(),
        Substitute.For<IAudioCapture>(),
        Substitute.For<ITranscriber>(),
        Substitute.For<ITextInjector>(),
        Substitute.For<IForegroundWindow>(),
        Substitute.For<INoteRepository>(),
        new NullPostProcessor(),
        NullLogger<DictationPipeline>.Instance);

    [Fact]
    public void Recien_construido_no_hay_nada_que_aplicar()
    {
        var view = Build(new Settings { CorrectVoseo = true });

        Assert.False(view.CorrectVoseoChangedSinceApplied);
    }

    [Fact]
    public void Tocar_el_checkbox_marca_que_hay_algo_para_aplicar()
    {
        var view = Build(new Settings { CorrectVoseo = false });

        view.CorrectVoseo = true;

        Assert.True(view.CorrectVoseoChangedSinceApplied);
    }

    /// <summary>
    /// The exact bug: guardar un ajuste que no toca CorrectVoseo (el atajo, el
    /// idioma, lo que sea) no puede quedar marcado como "hay que volver a
    /// intentar la descarga".
    /// </summary>
    [Fact]
    public void Volver_al_valor_original_deja_de_marcar_un_cambio_pendiente()
    {
        var view = Build(new Settings { CorrectVoseo = false });

        view.CorrectVoseo = true;
        view.CorrectVoseo = false;

        Assert.False(view.CorrectVoseoChangedSinceApplied);
    }

    /// <summary>
    /// A change the tray already applied on its own — <see cref="App.SetCorrectionEnabled"/>
    /// awaits <c>postProcessor.SetEnabledAsync</c> before ever calling
    /// <see cref="MainViewModel.ReflectCorrectVoseo"/> — must not look like a
    /// pending change the next time an unrelated setting is saved.
    /// </summary>
    [Fact]
    public void Reflejar_un_cambio_ya_aplicado_por_la_bandeja_no_deja_nada_pendiente()
    {
        var view = Build(new Settings { CorrectVoseo = false });

        view.ReflectCorrectVoseo(true);

        Assert.True(view.CorrectVoseo);
        Assert.False(view.CorrectVoseoChangedSinceApplied);
    }
}

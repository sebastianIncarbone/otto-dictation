using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Otto.App;
using Otto.App.ViewModels;
using Otto.Speech;
using Otto.Tts;

namespace Otto.Core.Tests;

/// <summary>
/// La sección de lectura en Ajustes.
///
/// <para>
/// Igual que <see cref="MainViewModelCorrectVoseoTests"/>, nunca invoca
/// <c>SaveSettingsCommand</c>: ese comando llama a <c>Autostart.Apply</c>, que escribe en
/// el <c>Run</c> de verdad del registro. <see cref="MainViewModel.ApplyTo"/> es pública y
/// separada del guardado justamente para que la decisión se pueda verificar sin ir a
/// ningún lado.
/// </para>
/// </summary>
public class MainViewModelReadingTests
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
    public void Toma_la_voz_y_la_entonacion_de_los_ajustes()
    {
        var view = Build(new Settings { ReadAloud = true, ReadingVoice = "es_MX-claude-high", ReadingVoicing = "estable" });

        Assert.True(view.ReadAloud);
        Assert.Equal("es_MX-claude-high", view.ReadingVoice.Id);
        Assert.Equal(PiperVoicing.Steady, view.ReadingVoicing);
    }

    [Fact]
    public void Una_voz_desconocida_en_el_config_no_deja_la_ventana_sin_voz()
    {
        // Resolver en vez de buscar estricto: config.json lo edita el usuario y lo escribió
        // una versión anterior. Un id retirado dejaría esta propiedad en null y la ventana
        // se caería al abrir Ajustes.
        var view = Build(new Settings { ReadingVoice = "es_XX-nadie-medium", ReadingVoicing = "turbo" });

        Assert.Equal(Voices.Default, view.ReadingVoice);
        Assert.Equal(PiperVoicing.Natural, view.ReadingVoicing);
    }

    [Fact]
    public void ApplyTo_escribe_los_tres_campos_de_lectura()
    {
        var view = Build();

        view.ReadAloud = true;
        view.ReadingVoice = Voices.All[3];
        view.ReadingVoicing = PiperVoicing.Measured;

        var applied = view.ApplyTo(new Settings());

        Assert.True(applied.ReadAloud);
        Assert.Equal(Voices.All[3].Id, applied.ReadingVoice);
        Assert.Equal("pausado", applied.ReadingVoicing);
    }

    [Fact]
    public void ApplyTo_no_pisa_lo_que_la_ventana_no_muestra()
    {
        // "Settings are amended, never rebuilt". La ventana no muestra el modelo de
        // Whisper, así que construir un Settings nuevo lo resetearía en silencio — que es
        // el bug que ya reseteaba el atajo en cada guardado.
        var view = Build();

        view.ReadAloud = true;

        var applied = view.ApplyTo(new Settings { Model = "base" });

        Assert.Equal("base", applied.Model);
        Assert.True(applied.ReadAloud);
    }

    [Fact]
    public void Recien_construido_no_hay_nada_que_aplicar()
    {
        Assert.False(Build(new Settings { ReadAloud = true }).ReadAloudChangedSinceApplied);
        Assert.False(Build(new Settings { ReadAloud = false }).ReadAloudChangedSinceApplied);
    }

    [Fact]
    public void Tildar_la_casilla_marca_que_hay_algo_que_aplicar()
    {
        // App toma un atajo global cuando esto se prende. Rehacer eso en cada guardado que
        // no tocó el casillero es trabajo que sólo puede fallar, nunca salir distinto.
        var view = Build(new Settings { ReadAloud = false });

        view.ReadAloud = true;

        Assert.True(view.ReadAloudChangedSinceApplied);
    }

    [Fact]
    public void Sin_instalador_no_ofrece_bajar_nada()
    {
        // Program.cs es el único que pasa el instalador. Sin él la respuesta honesta es
        // "no hay voz instalada y no hay por dónde bajarla", no una excepción.
        var view = Build();

        Assert.False(view.IsReadingVoiceInstalled);
        Assert.False(view.CanDownloadVoice);
    }

    [Fact]
    public void Cambiar_de_voz_limpia_el_estado_de_la_anterior()
    {
        // "La voz X quedó lista" debajo de un selector que ahora dice Y es una mentira
        // chiquita que igual manda a alguien a buscar un problema que no existe.
        var view = Build();

        view.ReadingVoice = Voices.All[1];

        Assert.False(view.HasReadingStatus);
    }
}

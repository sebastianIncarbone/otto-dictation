using Otto.App;
using Otto.Tts;

namespace Otto.Core.Tests;

/// <summary>
/// Los ajustes de la lectura en voz alta.
///
/// El caso que importa acá no es el primer arranque, es la actualización: todo el mundo
/// que ya tiene Otto instalado tiene un `config.json` escrito por una versión que no
/// sabía que la lectura existía.
/// </summary>
public class ReadingSettingsTests : IDisposable
{
    private readonly string path =
        Path.Combine(Path.GetTempPath(), "otto-ajustes-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        if (File.Exists(path)) File.Delete(path);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void La_lectura_viene_apagada_de_fabrica()
    {
        // Asimétrico con CorrectVoseo a propósito: el modelo de corrección ya se bajó en
        // el primer arranque, así que prenderla no cuesta nada que el usuario no haya
        // pagado. Una voz son ~110 MB que deliberadamente NO se bajan al arrancar.
        Assert.False(new Settings().ReadAloud);
    }

    [Fact]
    public void La_voz_por_defecto_es_la_del_catalogo_no_un_literal()
    {
        var settings = new Settings();

        Assert.Equal(Voices.Default.Id, settings.ReadingVoice);
        Assert.Equal(PiperVoicing.Natural.Id, settings.ReadingVoicing);
    }

    [Fact]
    public void El_atajo_de_lectura_no_es_el_de_dictado()
    {
        var settings = new Settings();

        Assert.NotEqual(settings.ToBinding(), settings.ToReadingBinding());
        Assert.Equal(HotkeyBinding.DefaultReading, settings.ToReadingBinding());
    }

    [Fact]
    public void Un_config_viejo_sin_los_campos_de_lectura_carga_con_los_defaults()
    {
        // Un archivo escrito por una versión anterior a esta función. Si los defaults no
        // salieran del record, ReadingVoice llegaría como null y el sintetizador buscaría
        // un archivo llamado ".onnx".
        File.WriteAllText(path, """
            {
              "Modifiers": 3,
              "VirtualKey": 32,
              "HotkeyLabel": "Ctrl+Alt+Espacio",
              "Language": "es",
              "CorrectVoseo": true,
              "CorrectionIdleUnloadMinutes": 15
            }
            """);

        var settings = new SettingsStore(path).Load();

        Assert.False(settings.ReadAloud);
        Assert.Equal(Voices.Default.Id, settings.ReadingVoice);
        Assert.Equal(PiperVoicing.Natural.Id, settings.ReadingVoicing);
        Assert.Equal(HotkeyBinding.DefaultReading, settings.ToReadingBinding());

        // Y no pisó lo que el archivo sí traía.
        Assert.True(settings.CorrectVoseo);
        Assert.Equal(15, settings.CorrectionIdleUnloadMinutes);
    }

    [Fact]
    public void Una_voz_retirada_en_el_config_no_impide_arrancar()
    {
        // config.json lo puede editar el usuario, y una versión futura puede sacar una voz
        // del catálogo. Resolver a la argentina es la respuesta segura y además la
        // correcta; tirar acá dejaría a alguien sin aplicación y sin forma de arreglarla
        // desde adentro de Otto.
        File.WriteAllText(path, """
            { "ReadAloud": true, "ReadingVoice": "es_XX-nadie-medium", "ReadingVoicing": "turbo" }
            """);

        var settings = new SettingsStore(path).Load();

        Assert.Equal(Voices.Default, Voices.Resolve(settings.ReadingVoice));
        Assert.Equal(PiperVoicing.Natural, PiperVoicing.Resolve(settings.ReadingVoicing));
    }

    [Fact]
    public void Los_ajustes_de_lectura_sobreviven_una_reescritura()
    {
        // "Settings are amended, never rebuilt": guardar desde una pantalla que no conoce
        // estos campos no los puede resetear.
        var store = new SettingsStore(path);

        store.Save(new Settings { ReadAloud = true, ReadingVoice = "es_MX-claude-high", ReadingVoicing = "estable" });

        var reloaded = store.Load() with { Language = "en" };
        store.Save(reloaded);

        var again = store.Load();

        Assert.True(again.ReadAloud);
        Assert.Equal("es_MX-claude-high", again.ReadingVoice);
        Assert.Equal("estable", again.ReadingVoicing);
        Assert.Equal("en", again.Language);
    }
}

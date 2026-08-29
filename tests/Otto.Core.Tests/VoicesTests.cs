using Otto.Tts;

namespace Otto.Core.Tests;

/// <summary>
/// El catálogo de voces, y la única que no está en discusión.
/// </summary>
public class VoicesTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), "otto-voces-" + Guid.NewGuid().ToString("N"));

    public VoicesTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void La_voz_por_defecto_es_la_argentina()
    {
        // Otto existe porque el dictado de Windows es malo con el rioplatense. Una voz
        // de lectura peninsular por defecto sería esa contradicción hecha audible, y
        // daniela es la única argentina de todo el catálogo de Piper.
        Assert.Equal("es_AR-daniela-high", Voices.Default.Id);
        Assert.Equal("Rioplatense", Voices.Default.Accent);
    }

    [Fact]
    public void Una_voz_desconocida_cae_en_la_argentina_en_vez_de_explotar()
    {
        // Este valor sale de config.json, que el usuario puede editar y que una versión
        // vieja de Otto pudo haber escrito. Un id que este build no conoce no puede
        // impedir que la aplicación arranque.
        Assert.Equal(Voices.Default, Voices.Resolve("es_XX-nadie-medium"));
        Assert.Equal(Voices.Default, Voices.Resolve(null));
        Assert.Equal(Voices.Default, Voices.Resolve("   "));
    }

    [Fact]
    public void Resuelve_sin_importar_las_mayusculas()
    {
        Assert.Equal("es_MX-claude-high", Voices.Resolve("ES_MX-CLAUDE-HIGH").Id);
    }

    [Fact]
    public void Una_voz_esta_instalada_solo_cuando_estan_los_dos_archivos()
    {
        var voice = Voices.Default;

        Assert.False(voice.IsInstalled(directory));

        // Solo el modelo: Piper resuelve el config desde la ruta del modelo y falla con
        // un error de parseo, no de archivo faltante. Contar esto como instalado manda
        // a cualquiera que lo debuguee al lugar equivocado.
        File.WriteAllText(voice.ModelPath(directory), "");
        Assert.False(voice.IsInstalled(directory));

        File.WriteAllText(voice.ConfigPath(directory), "");
        Assert.True(voice.IsInstalled(directory));
    }

    [Fact]
    public void El_config_se_baja_del_mismo_lugar_que_el_modelo_con_json_al_final()
    {
        var voice = Voices.Default;

        Assert.Equal(voice.Url + ".json", voice.ConfigUrl);
        Assert.EndsWith("/es/es_AR/daniela/high/es_AR-daniela-high.onnx", voice.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void Instaladas_devuelve_solo_las_que_estan_completas()
    {
        var complete = Voices.All[1];
        var halfway = Voices.All[2];

        File.WriteAllText(complete.ModelPath(directory), "");
        File.WriteAllText(complete.ConfigPath(directory), "");
        File.WriteAllText(halfway.ModelPath(directory), "");

        var installed = Voices.Installed(directory);

        Assert.Equal(complete, Assert.Single(installed));
    }

    [Fact]
    public void Ningun_id_del_catalogo_esta_repetido()
    {
        Assert.Equal(Voices.All.Count, Voices.All.Select(voice => voice.Id).Distinct().Count());
    }
}

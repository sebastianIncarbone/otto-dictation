using System.Globalization;
using Otto.Tts;

namespace Otto.Core.Tests;

/// <summary>
/// Lo que quedó del "nivel de esfuerzo": cómo se samplea un mismo modelo.
///
/// La escalera de modelos livianos no sobrevivió a la medición — daniela existe solo en
/// high, así que bajar de tier cuesta el acento — y el motor lento tampoco, porque genera
/// más despacio de lo que se habla. Estas tres perillas son el ajuste que sí se puede
/// ofrecer sin pagar ninguna de esas dos cosas.
/// </summary>
public class PiperVoicingTests
{
    [Fact]
    public void Los_numeros_van_con_punto_aunque_la_maquina_use_coma()
    {
        // Este es el bug que el test existe para atrapar. En es-AR el formato por defecto
        // escribe "0,8"; Piper lo parsea como "0" y devuelve una lectura con todas las
        // perillas estocásticas en cero: más apagada, más plana, y sin un solo error en
        // ningún lado que lo delate.
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("es-AR");

            var arguments = PiperVoicing.Natural.Arguments().ToArray();

            Assert.Contains("0.8", arguments);
            Assert.DoesNotContain(arguments, argument => argument.Contains(','));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Cada_perilla_va_con_su_bandera_adelante()
    {
        var arguments = PiperVoicing.Steady.Arguments().ToArray();

        Assert.Equal(6, arguments.Length);
        Assert.Equal("--noise_w", arguments[0]);
        Assert.Equal("--noise_scale", arguments[2]);
        Assert.Equal("--length_scale", arguments[4]);
    }

    [Fact]
    public void Un_preset_desconocido_cae_en_natural_en_vez_de_explotar()
    {
        Assert.Equal(PiperVoicing.Natural, PiperVoicing.Resolve("turbo"));
        Assert.Equal(PiperVoicing.Natural, PiperVoicing.Resolve(null));
        Assert.Equal(PiperVoicing.Natural, PiperVoicing.Resolve(""));
    }

    [Fact]
    public void Resuelve_cada_preset_del_catalogo_por_su_id()
    {
        Assert.All(PiperVoicing.All, voicing => Assert.Equal(voicing, PiperVoicing.Resolve(voicing.Id)));
    }

    [Fact]
    public void Solo_pausado_toca_la_velocidad()
    {
        // LengthScale es velocidad de habla, no variabilidad. "Cuesta seguirlo" y "suena
        // inestable" son quejas distintas con arreglos distintos, y mezclarlas en un solo
        // preset deja al usuario sin poder arreglar una sin la otra.
        Assert.Equal(1.0, PiperVoicing.Natural.LengthScale);
        Assert.Equal(1.0, PiperVoicing.Balanced.LengthScale);
        Assert.Equal(1.0, PiperVoicing.Steady.LengthScale);
        Assert.True(PiperVoicing.Measured.LengthScale > 1.0);
    }

    [Fact]
    public void El_ruido_baja_a_medida_que_el_preset_se_vuelve_mas_estable()
    {
        Assert.True(PiperVoicing.Natural.NoiseW > PiperVoicing.Balanced.NoiseW);
        Assert.True(PiperVoicing.Balanced.NoiseW > PiperVoicing.Steady.NoiseW);
    }
}

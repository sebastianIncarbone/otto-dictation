using System.Globalization;
using Otto.App.Views;

namespace Otto.Core.Tests;

/// <summary>
/// Cuántas columnas entran, expresado como cuánto mide una nota.
/// </summary>
public class NoteColumnsTests
{
    private static object Width(double available) =>
        NoteColumns.ItemWidth.Convert(available, typeof(double), null, CultureInfo.InvariantCulture);

    [Fact]
    public void Angosta_una_sola_columna()
    {
        // El ancho por defecto de la ventana.
        Assert.Equal(560d, Width(560));
    }

    [Fact]
    public void Ancha_se_parte_al_medio()
    {
        Assert.Equal(450d, Width(900));
    }

    [Fact]
    public void El_corte_incluye_su_propio_valor()
    {
        Assert.Equal(NoteColumns.Splits / 2, Width(NoteColumns.Splits));
        Assert.Equal(NoteColumns.Splits - 1, Width(NoteColumns.Splits - 1));
    }

    [Fact]
    public void Sin_ancho_todavia_la_nota_se_mide_sola()
    {
        // El primer paso de layout llega sin ancho. Contestar 0 le daría a cada nota
        // un ancho de nada y una pantalla vacía de la que no se vuelve.
        Assert.Equal(double.NaN, Width(0));
        Assert.Equal(double.NaN, Width(-1));
    }

    [Fact]
    public void El_ancho_no_vuelve_para_atras()
    {
        Assert.Throws<NotSupportedException>(() =>
            NoteColumns.ItemWidth.ConvertBack(420d, typeof(double), null, CultureInfo.InvariantCulture));
    }
}

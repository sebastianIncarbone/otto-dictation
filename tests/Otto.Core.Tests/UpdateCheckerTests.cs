using Otto.App;

namespace Otto.Core.Tests;

/// <summary>
/// Comparar versiones como texto es la forma clásica de que un chequeo de
/// actualizaciones empiece a mentir en la décima release y nadie se entere por
/// meses. Estos tests existen para que eso no pase en silencio.
/// </summary>
public class UpdateCheckerTests
{
    [Theory]
    [InlineData("0.2.0", "0.1.0")]
    [InlineData("1.0.0", "0.9.9")]
    [InlineData("0.1.1", "0.1.0")]
    public void Detecta_una_version_mas_nueva(string candidate, string current) =>
        Assert.True(UpdateChecker.IsNewer(candidate, current));

    [Fact]
    public void Diez_es_mayor_que_nueve()
    {
        // Como texto, "0.10.0" < "0.9.0". Este es EL caso que rompe la comparación
        // ingenua, y no aparece hasta la décima release.
        Assert.True(UpdateChecker.IsNewer("0.10.0", "0.9.0"));
        Assert.False(UpdateChecker.IsNewer("0.9.0", "0.10.0"));
    }

    [Theory]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("0.1.0", "0.2.0")]
    public void No_ofrece_bajar_de_version(string candidate, string current) =>
        Assert.False(UpdateChecker.IsNewer(candidate, current));

    [Fact]
    public void Tolera_versiones_incompletas()
    {
        // Alguien va a etiquetar "v2" alguna vez.
        Assert.True(UpdateChecker.IsNewer("2", "1.9.9"));
        Assert.True(UpdateChecker.IsNewer("1.1", "1.0.5"));
    }

    [Fact]
    public void Ignora_el_sufijo_de_prelanzamiento() =>
        Assert.True(UpdateChecker.IsNewer("0.2.0-beta.1", "0.1.0"));

    [Fact]
    public void Una_etiqueta_ilegible_no_ofrece_actualizar() =>
        Assert.False(UpdateChecker.IsNewer("ultima", "0.1.0"));

    [Fact]
    public void La_version_del_ensamblado_no_es_la_de_fabrica()
    {
        // Si esto vuelve a ser 1.0.0, es que <Version> se perdió del csproj y el
        // chequeo de actualizaciones va a decir "estás al día" para siempre.
        Assert.NotEqual("1.0.0", UpdateChecker.Current);
    }
}

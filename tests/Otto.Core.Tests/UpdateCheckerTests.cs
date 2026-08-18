using Otto.App;

namespace Otto.Core.Tests;

/// <summary>
/// Comparing versions as strings is the classic way an update check starts lying
/// at the tenth release and nobody notices for months. These tests exist so that
/// cannot happen quietly.
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
        // As strings, "0.10.0" < "0.9.0". This is THE case that breaks the naive
        // comparison, and it does not show up until the tenth release.
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
        // If this goes back to 1.0.0, <Version> was lost from the build and the
        // update check will answer "you are up to date" forever.
        Assert.NotEqual("1.0.0", UpdateChecker.Current);
    }
}

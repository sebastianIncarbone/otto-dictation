using Otto.App.Views;

namespace Otto.Core.Tests;

/// <summary>
/// El resaltado tiene que coincidir con lo que hizo la búsqueda, no con lo que
/// parezca razonable: <c>SqliteNoteRepository.ToMatchExpression</c> le pasa a FTS5
/// cada término entrecomillado con un <c>*</c>, o sea prefijo por palabra.
/// </summary>
public class HighlightTests
{
    private static string Painted(string text, string query) =>
        string.Concat(Highlight.Split(text, query).Where(s => s.Match).Select(s => s.Text));

    private static string Whole(string text, string query) =>
        string.Concat(Highlight.Split(text, query).Select(s => s.Text));

    [Fact]
    public void Sin_busqueda_no_se_pinta_nada()
    {
        var only = Assert.Single(Highlight.Split("Sacá el retry del downloader.", ""));

        Assert.False(only.Match);
    }

    [Fact]
    public void Pinta_el_termino_donde_aparece()
    {
        Assert.Equal("downloader", Painted("Sacá el retry del downloader.", "downloader"));
    }

    [Fact]
    public void Pinta_por_prefijo_como_busca_FTS5()
    {
        // La búsqueda devolvió esta nota por "down"*, así que el resaltado tiene que
        // poder mostrar por qué.
        Assert.Equal("downloader", Painted("Sacá el retry del downloader.", "down"));
    }

    [Fact]
    public void No_pinta_en_el_medio_de_una_palabra()
    {
        // "load" está adentro de "downloader", pero "load"* nunca habría traído esta
        // nota. Pintarlo sería afirmar una coincidencia que la consulta no hizo.
        Assert.Equal("", Painted("Sacá el retry del downloader.", "load"));
    }

    [Fact]
    public void Una_sola_letra_no_prende_la_nota_entera()
    {
        // El caso que hace que buscar por subcadena sea inaceptable: con "a" suelta,
        // media nota quedaría pintada mientras alguien todavía está escribiendo.
        Assert.Equal("arranca", Painted("Dale, arranca eso y seguí", "a"));
    }

    [Fact]
    public void Ignora_mayusculas()
    {
        Assert.Equal("Retry", Painted("Retry del downloader", "retry"));
    }

    [Fact]
    public void Ignora_acentos_igual_que_el_tokenizador()
    {
        // FTS5 pliega los diacríticos, así que "correccion" es una consulta que de
        // verdad devuelve esta nota. Sin esto la traería y no diría por qué.
        Assert.Equal("corrección", Painted("La corrección del voseo", "correccion"));
    }

    [Fact]
    public void Varios_terminos_se_pintan_todos()
    {
        var painted = Painted("Sacá el retry del downloader", "retry downloader");

        Assert.Equal("retrydownloader", painted);
    }

    [Fact]
    public void El_texto_sobrevive_entero_al_corte()
    {
        const string text = "Sacá el retry del downloader: ya reintenta solo.";

        // Lo que se arma con los pedazos tiene que ser exactamente lo que entró.
        // Un resaltado que come o duplica una letra corrompe la nota en pantalla.
        Assert.Equal(text, Whole(text, "retry downloader ya"));
    }

    [Fact]
    public void Un_termino_que_no_esta_no_pinta_nada()
    {
        Assert.Equal("", Painted("Sacá el retry del downloader", "webhook"));
    }

    [Fact]
    public void Texto_vacio_no_explota()
    {
        var only = Assert.Single(Highlight.Split("", "downloader"));

        Assert.Equal("", only.Text);
    }

    [Fact]
    public void Un_termino_repetido_se_pinta_cada_vez()
    {
        Assert.Equal("holahola", Painted("hola, hola de nuevo", "hola"));
    }
}

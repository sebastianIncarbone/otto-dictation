using Otto.Tts;

namespace Otto.Core.Tests;

/// <summary>
/// How a reading is cut up before it reaches the engine.
///
/// Every case here is about one of two failures: dead air before the first word, or a
/// fragment that costs a whole process launch and produces nothing.
/// </summary>
public class SentencesTests
{
    [Fact]
    public void El_primer_fragmento_es_mas_corto_que_los_siguientes()
    {
        const string text =
            "Cuando termina la dictada, Otto guarda la nota y la deja lista para buscar, " +
            "porque dictar y guardar son la misma acción y no hay un paso aparte. " +
            "Después la podés editar, exportar o escuchar.";

        var chunks = Sentences.Split(text);

        Assert.True(chunks.Count > 1, "un texto largo tiene que partirse");

        // El primero se corta corto a propósito: es el único que se espera en silencio.
        Assert.True(chunks[0].Length < 60, $"el primer fragmento mide {chunks[0].Length}");
    }

    [Fact]
    public void Corta_el_primer_fragmento_en_una_coma_y_los_demas_no()
    {
        const string text =
            "Cuando termina la dictada, Otto guarda la nota, la indexa, y la deja lista para buscar.";

        var chunks = Sentences.Split(text);

        Assert.StartsWith("Cuando termina la dictada,", chunks[0], StringComparison.Ordinal);

        // Una coma más adelante no alcanza para cortar: partir al medio de una oración
        // cuesta prosodia y ya no compra nada, porque ese fragmento se genera mientras
        // suena el anterior.
        Assert.DoesNotContain(chunks.Skip(1), chunk => chunk.EndsWith(','));
    }

    [Fact]
    public void Aplana_los_saltos_de_linea()
    {
        // Piper trata los saltos de línea como límites de utterance y con -f solo
        // sobrevive el último. Si un salto llegara entero al motor, el usuario
        // escucharía únicamente el renglón final, con un WAV válido como prueba.
        var chunks = Sentences.Split("Primer renglón del texto.\r\nSegundo renglón del texto.");

        Assert.DoesNotContain(chunks, chunk => chunk.Contains('\n') || chunk.Contains('\r'));
        Assert.Contains(chunks, chunk => chunk.Contains("Segundo renglón", StringComparison.Ordinal));
    }

    [Fact]
    public void Descarta_los_fragmentos_que_no_tienen_ni_una_letra()
    {
        var chunks = Sentences.Split("Bueno, listo. ... ¿Y ahora qué hacemos con todo esto?");

        // Un fragmento de pura puntuación produce un WAV de puro silencio, y cuesta
        // el arranque completo de un proceso averiguarlo.
        Assert.All(chunks, chunk => Assert.True(chunk.Any(char.IsLetterOrDigit), $"«{chunk}» no tiene ni una letra"));
    }

    [Fact]
    public void Parte_un_parrafo_sin_un_solo_punto()
    {
        // Sin el máximo, un párrafo empalmado con comas bloquearía la lectura entera
        // esperando un punto que nunca llega.
        var text = string.Join(", ", Enumerable.Repeat("seguimos hablando sin cerrar la idea", 40));

        var chunks = Sentences.Split(text);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 300, $"un fragmento mide {chunk.Length}"));
    }

    [Fact]
    public void Un_texto_vacio_no_produce_fragmentos()
    {
        Assert.Empty(Sentences.Split(""));
        Assert.Empty(Sentences.Split("   \r\n  "));
    }

    [Fact]
    public void No_pierde_texto_al_partir()
    {
        const string text = "Primera oración corta. Y una segunda oración, bastante más larga que la primera.";

        var rebuilt = string.Concat(Sentences.Split(text).Select(chunk => chunk.Trim()));
        var expected = string.Concat(text.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        Assert.Equal(expected, string.Concat(rebuilt.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
    }
}

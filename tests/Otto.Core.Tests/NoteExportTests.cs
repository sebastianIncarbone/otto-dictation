using System.Text;
using NSubstitute;
using Otto.App;
using Otto.App.ViewModels;
using Otto.Core;

namespace Otto.Core.Tests;

/// <summary>
/// Lo que sale del archivo. Un dictado que vuelve mutilado es la única falla que
/// una exportación no tiene perdón: el original puede haberse borrado justo
/// después, confiando en la copia.
/// </summary>
public class NoteExportTests
{
    private readonly INoteRepository repository = Substitute.For<INoteRepository>();

    private NoteViewModel Note(string title, string text, string source = "Code") =>
        new(
            new Note(1, title, text,
                new DateTimeOffset(2026, 8, 18, 11, 42, 0, TimeSpan.Zero),
                DateTimeOffset.UtcNow,
                new DictationContext(source, "Program.cs"),
                TimeSpan.FromSeconds(14)),
            repository,
            () => null);

    [Fact]
    public void El_texto_plano_lleva_titulo_datos_y_nota()
    {
        var output = NoteExport.ToPlainText([Note("Retry del downloader", "Sacá el retry.")]);

        Assert.Contains("Retry del downloader", output);
        Assert.Contains("Code", output);
        Assert.Contains("14 s", output);
        Assert.Contains("Sacá el retry.", output);
    }

    [Fact]
    public void El_texto_plano_no_inventa_sintaxis()
    {
        var output = NoteExport.ToPlainText([Note("Uno", "Primera."), Note("Dos", "Segunda.")]);

        // Un .txt que se pone a dibujar separadores es un .txt peor, no un .md mejor.
        Assert.DoesNotContain("#", output);
        Assert.DoesNotContain("---", output);
        Assert.DoesNotContain("_", output);
    }

    [Fact]
    public void El_markdown_pone_cada_nota_en_su_seccion()
    {
        var note = Note("Retry del downloader", "Sacá el retry.");
        var output = NoteExport.ToMarkdown([note]);

        Assert.Contains("## Retry del downloader", output);
        Assert.Contains("Sacá el retry.", output);

        // Contra el Subtitle de la nota y no contra un horario escrito a mano: la
        // hora que se exporta es local, igual que la que se ve en pantalla, así que
        // fijar "11:42" ata el test al huso de quien lo corre.
        Assert.Contains($"_{note.Subtitle}_", output);
    }

    [Fact]
    public void Una_nota_sin_titulo_exporta_el_relleno()
    {
        var output = NoteExport.ToMarkdown([Note("", "Sin nombre.")]);

        Assert.Contains("## Sin título", output);
    }

    [Fact]
    public void El_texto_de_la_nota_sale_tal_cual()
    {
        // Sin escapar: defenderse de un dictado que arranca con almohadilla es raro,
        // y el precio de defenderse es un archivo lleno de barras invertidas delante
        // de puntuación común, que no es raro para nada.
        const string dictated = "El 50% de los *tests* fallan. ¿Y el resto? Ni idea.";

        Assert.Contains(dictated, NoteExport.ToMarkdown([Note("Ojo", dictated)]));
        Assert.Contains(dictated, NoteExport.ToPlainText([Note("Ojo", dictated)]));
    }

    [Fact]
    public void Exportar_varias_las_separa_pero_no_las_mezcla()
    {
        var output = NoteExport.ToPlainText([Note("Uno", "Primera."), Note("Dos", "Segunda.")]);

        Assert.Contains("Primera.\r\n\r\nDos", output);
    }

    [Fact]
    public void Los_saltos_son_de_Windows()
    {
        var output = NoteExport.ToPlainText([Note("Uno", "Primera.")]);

        // La app es sólo Windows y estos archivos los abren herramientas de Windows.
        Assert.Contains("\r\n", output);
        Assert.DoesNotContain("\n\n\n", output.Replace("\r", ""));
    }

    [Fact]
    public void Exportar_nada_da_un_archivo_vacio_y_no_una_excepcion()
    {
        Assert.Equal("", NoteExport.ToPlainText([]));
        Assert.Equal("", NoteExport.ToMarkdown([]));
    }

    [Fact]
    public void El_nombre_sugerido_lleva_la_fecha_y_la_extension()
    {
        var when = new DateTimeOffset(2026, 8, 19, 15, 0, 0, TimeSpan.Zero);

        Assert.Equal("notas-otto-2026-08-19.txt", NoteExport.SuggestedName(when, markdown: false));
        Assert.Equal("notas-otto-2026-08-19.md", NoteExport.SuggestedName(when, markdown: true));
    }

    [Fact]
    public void El_archivo_declara_que_es_UTF8()
    {
        // Todo lo que exporta Otto está lleno de acentos y eñes. Un editor de Windows
        // que adivina mal la codificación convierte "corrección" en basura, y la marca
        // es lo que le saca la posibilidad de adivinar.
        Assert.Equal([0xEF, 0xBB, 0xBF], NoteExport.Encoding.GetPreamble());
        Assert.Equal("corrección", NoteExport.Encoding.GetString(
            NoteExport.Encoding.GetBytes("corrección")));
    }
}

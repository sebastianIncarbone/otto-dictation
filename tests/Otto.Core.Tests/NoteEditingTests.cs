using NSubstitute;
using Otto.App.ViewModels;
using Otto.Core;

namespace Otto.Core.Tests;

/// <summary>
/// Reading and editing a note are two different states, and everything that can
/// lose what somebody typed lives on the boundary between them.
/// </summary>
public class NoteEditingTests
{
    private readonly INoteRepository repository = Substitute.For<INoteRepository>();

    private NoteViewModel Build(string title = "Retry del downloader", string text = "Sacá el retry.") =>
        new(
            new Note(1, title, text, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                new DictationContext("Code", "Program.cs"), TimeSpan.FromSeconds(14)),
            repository,
            () => null);

    [Fact]
    public void Una_nota_arranca_en_lectura()
    {
        Assert.False(Build().IsEditing);
    }

    [Fact]
    public void Abrirla_la_pone_en_edicion()
    {
        var note = Build();

        note.BeginEditCommand.Execute(null);

        Assert.True(note.IsEditing);
    }

    [Fact]
    public void Escape_descarta_lo_escrito_y_vuelve_a_lectura()
    {
        var note = Build(title: "Original", text: "Texto original.");
        note.BeginEditCommand.Execute(null);

        note.Title = "Otra cosa";
        note.Text = "Otro texto.";

        note.CancelEditCommand.Execute(null);

        Assert.Equal("Original", note.Title);
        Assert.Equal("Texto original.", note.Text);
        Assert.False(note.IsDirty);
        Assert.False(note.IsEditing);
    }

    [Fact]
    public async Task Guardar_deja_la_nota_en_lectura_y_limpia()
    {
        var note = Build();
        note.BeginEditCommand.Execute(null);
        note.Text = "Corregido.";

        await note.SaveCommand.ExecuteAsync(null);

        await repository.Received(1).UpdateAsync(1, note.Title, "Corregido.");
        Assert.False(note.IsDirty);
        Assert.False(note.IsEditing);
    }

    [Fact]
    public async Task Despues_de_guardar_escape_vuelve_a_lo_guardado_y_no_a_lo_viejo()
    {
        var note = Build(text: "Texto original.");
        note.BeginEditCommand.Execute(null);

        note.Text = "Primera corrección.";
        await note.SaveCommand.ExecuteAsync(null);

        // Lo guardado es el nuevo punto de retorno. Si CancelEdit volviera a lo que
        // trajo el repositorio al construirse, Escape desharía una edición que ya
        // está en disco y la pantalla mentiría sobre lo que hay guardado.
        note.BeginEditCommand.Execute(null);
        note.Text = "Segunda, sin guardar.";
        note.CancelEditCommand.Execute(null);

        Assert.Equal("Primera corrección.", note.Text);
    }

    [Fact]
    public void Cerrar_el_editor_por_ir_a_otra_nota_no_pisa_lo_no_guardado()
    {
        var note = Build();
        note.BeginEditCommand.Execute(null);
        note.Text = "A medio escribir.";

        note.CloseEditor();

        // Sigue abierta, y con el texto intacto: nadie pidió descartarlo.
        Assert.True(note.IsEditing);
        Assert.Equal("A medio escribir.", note.Text);
    }

    [Fact]
    public void Cerrar_el_editor_sin_cambios_si_cierra()
    {
        var note = Build();
        note.BeginEditCommand.Execute(null);

        note.CloseEditor();

        Assert.False(note.IsEditing);
    }

    [Fact]
    public void Una_nota_sin_titulo_lo_dice_y_se_deja_distinguir()
    {
        var note = Build(title: "");

        Assert.Equal("Sin título", note.Heading);
        Assert.False(note.HasTitle);

        // Y alguien que la llame así de verdad tiene título: la vista apaga el
        // relleno por HasTitle y no por comparar el texto.
        note.Title = "Sin título";

        Assert.True(note.HasTitle);
    }

    [Fact]
    public void El_renglon_de_datos_lleva_cuando_de_donde_y_cuanto()
    {
        var note = Build();

        Assert.Contains("Code", note.Subtitle);
        Assert.Contains("14 s", note.Subtitle);
        Assert.Equal(3, note.Subtitle.Split('·').Length);
    }

    [Fact]
    public void Un_dictado_de_menos_de_un_segundo_no_reporta_duracion()
    {
        var note = new NoteViewModel(
            new Note(1, "", "Ya.", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                new DictationContext("Code", "Program.cs"), TimeSpan.FromMilliseconds(400)),
            repository,
            () => null);

        // "0 s" se lee como una falla, no como una nota muy corta.
        Assert.Equal(2, note.Subtitle.Split('·').Length);
    }
}

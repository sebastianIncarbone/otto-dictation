using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Otto.App;
using Otto.App.ViewModels;
using Otto.Core;
using Otto.Speech;

namespace Otto.Core.Tests;

/// <summary>
/// Elegir varias notas y actuar sobre el lote. Casi todo lo que se puede romper
/// acá es irreversible, así que lo que se prueba es qué entra y qué sale de la
/// selección — no cómo se ve.
/// </summary>
public class NoteSelectionTests
{
    private readonly INoteRepository repository = Substitute.For<INoteRepository>();

    private static Note Note(long id, string text) =>
        new(id, "", text, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new DictationContext("Code", "Program.cs"), TimeSpan.FromSeconds(5));

    private async Task<MainViewModel> ListOfAsync(int count)
    {
        repository
            .RecentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, count).Select(i => Note(i, $"Nota {i}")).ToList());

        var availability = Substitute.For<IHotkeyAvailability>();
        availability.IsAvailable(Arg.Any<HotkeyBinding>()).Returns(true);

        var view = new MainViewModel(
            repository,
            new DictationPipeline(
                Substitute.For<IHotkeyService>(),
                Substitute.For<IAudioCapture>(),
                Substitute.For<ITranscriber>(),
                Substitute.For<ITextInjector>(),
                Substitute.For<IForegroundWindow>(),
                Substitute.For<INoteRepository>(),
                new NullPostProcessor(),
                NullLogger<DictationPipeline>.Instance),
            new SettingsStore(Path.Combine(Path.GetTempPath(), "otto-tests-no-escribe.json")),
            new Settings(),
            databasePath: "",
            clipboard: () => null,
            provisioningOptions: new ProvisioningOptions
            {
                ModelsDirectory = "", SpeechFileName = "", VadFileName = "", Label = "", Size = "",
            },
            availability);

        await view.ReloadAsync();

        return view;
    }

    [Fact]
    public async Task La_lista_arranca_leyendo_y_no_eligiendo()
    {
        var view = await ListOfAsync(3);

        Assert.False(view.IsSelecting);
        Assert.All(view.Notes, note => Assert.False(note.IsSelecting));
    }

    [Fact]
    public async Task Entrar_en_seleccion_alcanza_a_todas_las_notas()
    {
        var view = await ListOfAsync(3);

        view.StartSelectingCommand.Execute(null);

        Assert.All(view.Notes, note => Assert.True(note.IsSelecting));
    }

    [Fact]
    public async Task Entrar_en_seleccion_cierra_el_editor_abierto()
    {
        var view = await ListOfAsync(3);
        view.Notes[0].ActivateCommand.Execute(null);

        Assert.True(view.Notes[0].IsEditing);

        view.StartSelectingCommand.Execute(null);

        // Una fila en dos estados a la vez, con un Guardar entre botones que actúan
        // sobre otras notas, es una pantalla que no sabe qué está preguntando.
        Assert.False(view.Notes[0].IsEditing);
    }

    [Fact]
    public async Task Tocar_una_nota_mientras_se_elige_la_marca_en_vez_de_abrirla()
    {
        var view = await ListOfAsync(3);
        view.StartSelectingCommand.Execute(null);

        view.Notes[1].ActivateCommand.Execute(null);

        Assert.True(view.Notes[1].IsSelected);
        Assert.False(view.Notes[1].IsEditing);

        // Y el segundo toque la desmarca: es lo mismo que tocar una casilla.
        view.Notes[1].ActivateCommand.Execute(null);

        Assert.False(view.Notes[1].IsSelected);
    }

    [Fact]
    public async Task La_cuenta_sigue_a_lo_marcado()
    {
        var view = await ListOfAsync(3);
        view.StartSelectingCommand.Execute(null);

        Assert.Equal(0, view.SelectedCount);
        Assert.False(view.HasSelection);

        view.Notes[0].IsSelected = true;

        Assert.Equal(1, view.SelectedCount);
        Assert.Equal("1 nota seleccionada", view.SelectionLabel);
        Assert.Equal("Eliminar 1", view.DeleteSelectedLabel);

        view.Notes[2].IsSelected = true;

        Assert.Equal("2 notas seleccionadas", view.SelectionLabel);
        Assert.True(view.HasSelection);
    }

    [Fact]
    public async Task Todas_marca_lo_que_hay_en_pantalla()
    {
        var view = await ListOfAsync(4);
        view.StartSelectingCommand.Execute(null);

        view.SelectAllCommand.Execute(null);

        Assert.Equal(4, view.SelectedCount);
    }

    [Fact]
    public async Task Cancelar_sale_y_se_lleva_las_marcas()
    {
        var view = await ListOfAsync(3);
        view.StartSelectingCommand.Execute(null);
        view.SelectAllCommand.Execute(null);

        view.CancelSelectingCommand.Execute(null);

        Assert.False(view.IsSelecting);

        // Y no quedan esperando: si sobrevivieran, el próximo Seleccionar arrancaría
        // con notas marcadas que alguien eligió hace rato y no tiene por qué recordar.
        Assert.Equal(0, view.SelectedCount);
        Assert.All(view.Notes, note => Assert.False(note.IsSelected));
    }

    [Fact]
    public async Task Eliminar_pregunta_antes_y_dice_cuantas()
    {
        var view = await ListOfAsync(3);
        view.StartSelectingCommand.Execute(null);
        view.Notes[0].IsSelected = true;
        view.Notes[1].IsSelected = true;

        view.ConfirmDeleteSelectedCommand.Execute(null);

        Assert.True(view.IsConfirmingDeleteSelected);
        Assert.Equal("Se borran 2 notas y no se pueden recuperar.", view.DeleteSelectedWarning);

        // Y preguntar no borra nada por su cuenta.
        await repository.DidNotReceive().DeleteAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cambiar_lo_marcado_retira_la_pregunta()
    {
        var view = await ListOfAsync(3);
        view.StartSelectingCommand.Execute(null);
        view.Notes[0].IsSelected = true;
        view.ConfirmDeleteSelectedCommand.Execute(null);

        view.Notes[1].IsSelected = true;

        // "Se borra 1 nota" se preguntó sobre una nota concreta. Contestar que sí
        // después de marcar otra sería contestar algo que nadie preguntó.
        Assert.False(view.IsConfirmingDeleteSelected);
    }

    [Fact]
    public async Task Eliminar_borra_solo_lo_marcado()
    {
        var view = await ListOfAsync(3);
        view.StartSelectingCommand.Execute(null);
        view.Notes[0].IsSelected = true;
        view.Notes[2].IsSelected = true;

        view.ConfirmDeleteSelectedCommand.Execute(null);
        await view.DeleteSelectedCommand.ExecuteAsync(null);

        await repository.Received(1).DeleteAsync(1, Arg.Any<CancellationToken>());
        await repository.Received(1).DeleteAsync(3, Arg.Any<CancellationToken>());
        await repository.DidNotReceive().DeleteAsync(2, Arg.Any<CancellationToken>());

        Assert.Single(view.Notes);
        Assert.Equal(2, view.Notes[0].Id);
    }

    [Fact]
    public async Task Despues_de_eliminar_la_lista_vuelve_a_leerse()
    {
        var view = await ListOfAsync(2);
        view.StartSelectingCommand.Execute(null);
        view.SelectAllCommand.Execute(null);

        await view.DeleteSelectedCommand.ExecuteAsync(null);

        // El lote se terminó con el lote: quedarse en modo selección sobre una lista
        // que ya no tiene nada de lo elegido es un modo sin nada que hacer.
        Assert.False(view.IsSelecting);
        Assert.Empty(view.Notes);
        Assert.True(view.IsEmpty);
    }

    [Fact]
    public async Task Una_nota_borrada_deja_de_contar()
    {
        var view = await ListOfAsync(3);
        view.StartSelectingCommand.Execute(null);
        view.Notes[0].IsSelected = true;

        var deleted = view.Notes[0];
        await view.DeleteSelectedCommand.ExecuteAsync(null);

        // Sigue viva como objeto, pero ya no está suscripta: si marcarla volviera a
        // mover la cuenta, la barra hablaría de notas que no existen.
        deleted.IsSelected = false;
        deleted.IsSelected = true;

        Assert.Equal(0, view.SelectedCount);
    }
}

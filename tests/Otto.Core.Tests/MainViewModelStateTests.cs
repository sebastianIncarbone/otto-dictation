using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Otto.App;
using Otto.App.ViewModels;
using Otto.Core;
using Otto.Speech;

namespace Otto.Core.Tests;

/// <summary>
/// Two defects pinned down here.
///
/// The first: <see cref="MainViewModel"/> used to bind <c>pipeline.StateChanged</c>
/// straight to a method named <c>OnStateChanged</c> — colliding with the
/// CommunityToolkit-generated hook of the same name for the
/// <c>[ObservableProperty] state</c> field. Firing that method directly from the event
/// never went through the generated <see cref="MainViewModel.State"/> property setter,
/// so the backing field was never written; <c>State</c> stayed frozen at whatever the
/// pipeline reported when the singleton view model was built, which is
/// <see cref="DictationState.Loading"/> on a first-run or provisioning launch — the
/// exact case the first test builds.
///
/// The second, downstream of a startup registration failure closed in the same slice:
/// once <c>State</c> is stuck at Loading forever, <see cref="MainViewModel.StatusText"/>
/// and <see cref="MainViewModel.EmptyMessage"/> would otherwise keep repeating
/// "Cargando modelo…" instead of saying what actually happened.
/// </summary>
public class MainViewModelStateTests
{
    private static MainViewModel Build(DictationPipeline pipeline) => new(
        Substitute.For<INoteRepository>(),
        pipeline,
        new SettingsStore(Path.Combine(Path.GetTempPath(), "otto-tests-no-escribe.json")),
        new Settings(),
        databasePath: "",
        clipboard: () => null,
        provisioningOptions: new ProvisioningOptions
        {
            ModelsDirectory = "", SpeechFileName = "", VadFileName = "", Label = "", Size = "",
        });

    private static DictationPipeline BuildPipeline(IHotkeyService? hotkey = null) => new(
        hotkey ?? Substitute.For<IHotkeyService>(),
        Substitute.For<IAudioCapture>(),
        Substitute.For<ITranscriber>(),
        Substitute.For<ITextInjector>(),
        Substitute.For<IForegroundWindow>(),
        Substitute.For<INoteRepository>(),
        new NullPostProcessor(),
        NullLogger<DictationPipeline>.Instance);

    /// <summary>
    /// Posted to Dispatcher.UIThread the same way <c>OnSaved</c> already is. Nothing in
    /// this headless test process is pumping the dispatcher's queue — there is no
    /// Avalonia <c>Application</c> — so <see cref="Dispatcher.RunJobs"/> is called
    /// directly to drain it instead of waiting on a loop that would otherwise never run.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task El_estado_del_view_model_sigue_al_pipeline_despues_de_construir()
    {
        var pipeline = BuildPipeline();
        var view = Build(pipeline);

        Assert.Equal(DictationState.Loading, view.State);

        await pipeline.StartAsync(HotkeyBinding.Default);
        await WaitForAsync(() => view.State == DictationState.Idle);

        Assert.Equal(DictationState.Idle, view.State);
        Assert.DoesNotContain("Cargando modelo", view.StatusText);
    }

    [Fact]
    public void Un_fallo_de_registro_pone_el_encabezado_en_modo_alerta_hasta_que_se_arregla()
    {
        var view = Build(BuildPipeline());

        Assert.False(view.HasHotkeyAlert);

        view.ShowHotkeyFailure(alreadyInUse: true);

        Assert.True(view.HasHotkeyAlert);
        Assert.Contains("ya lo está usando", view.StatusText);
        Assert.Contains("Configuración", view.StatusText);
        Assert.Equal(view.StatusText, view.EmptyMessage);
    }

    [Fact]
    public void Un_fallo_de_registro_no_atribuible_a_otra_app_tambien_es_visible()
    {
        var view = Build(BuildPipeline());

        view.ShowHotkeyFailure(alreadyInUse: false);

        Assert.True(view.HasHotkeyAlert);
        Assert.DoesNotContain("ya lo está usando", view.StatusText);
        Assert.Contains("No se pudo activar el atajo", view.StatusText);
    }

    [Fact]
    public void Con_una_busqueda_activa_la_lista_vacia_habla_de_la_busqueda_y_no_del_atajo()
    {
        // The alert belongs in the header, and it is already there. Repeating it in the
        // list slot costs the user the only signal that their query matched nothing.
        var view = Build(BuildPipeline());
        view.ShowHotkeyFailure(alreadyInUse: true);

        view.Search = "reunión";

        Assert.True(view.HasHotkeyAlert);
        Assert.Contains("reunión", view.EmptyMessage);
        Assert.DoesNotContain("No se pudo activar el atajo", view.EmptyMessage);
    }

    [Fact]
    public void Sin_busqueda_la_lista_vacia_no_manda_a_usar_un_atajo_que_no_se_registro()
    {
        // The default text tells the user to hold a hotkey that is not listening.
        var view = Build(BuildPipeline());

        view.ShowHotkeyFailure(alreadyInUse: true);

        Assert.DoesNotContain("Mantené", view.EmptyMessage);
        Assert.Equal(view.StatusText, view.EmptyMessage);
    }
}

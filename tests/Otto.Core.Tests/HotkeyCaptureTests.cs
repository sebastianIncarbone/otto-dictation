using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Otto.App;
using Otto.App.ViewModels;
using Otto.Core;
using Otto.Speech;

namespace Otto.Core.Tests;

/// <summary>
/// <see cref="MainViewModel.OfferKey"/> is the whole capture state machine, kept over
/// <see cref="Otto.Core"/> types precisely so it is testable with no Avalonia
/// <c>Application</c> — <c>MainWindow.axaml.cs</c> is nothing but a key-event translator
/// in front of it. These pin down the header (<see cref="MainViewModel.ListeningLabel"/>)
/// never being confusable with what capture currently holds
/// (<see cref="MainViewModel.HotkeyLabel"/>) — that confusion is the defect itself.
/// </summary>
public class HotkeyCaptureTests
{
    /// <summary>
    /// Registers <paramref name="registered"/> on the pipeline first, the way
    /// <c>StartAsync</c> does in production, so <c>ListeningLabel</c> and
    /// <c>HotkeyChangePending</c> have a real registered binding to compare against.
    /// <paramref name="stored"/> is what the settings on disk say, which is deliberately
    /// allowed to differ: identical values here would let a header that ignored
    /// <c>RegisteredHotkey</c> pass anyway.
    /// </summary>
    private static async Task<MainViewModel> BuildAsync(HotkeyBinding registered, Settings? stored = null)
    {
        var pipeline = new DictationPipeline(
            Substitute.For<IHotkeyService>(),
            Substitute.For<IAudioCapture>(),
            Substitute.For<ITranscriber>(),
            Substitute.For<ITextInjector>(),
            Substitute.For<IForegroundWindow>(),
            Substitute.For<INoteRepository>(),
            new NullPostProcessor(),
            NullLogger<DictationPipeline>.Instance);

        await pipeline.StartAsync(registered);

        return new MainViewModel(
            Substitute.For<INoteRepository>(),
            pipeline,
            new SettingsStore(Path.Combine(Path.GetTempPath(), "otto-tests-no-escribe.json")),
            stored ?? new Settings(),
            databasePath: "",
            clipboard: () => null,
            provisioningOptions: new ProvisioningOptions
            {
                ModelsDirectory = "", SpeechFileName = "", VadFileName = "", Label = "", Size = "",
            });
    }

    [Fact]
    public async Task Mantener_solo_modificadores_no_committea_nada_y_deja_la_captura_abierta()
    {
        var view = await BuildAsync(HotkeyBinding.Default);
        view.IsCapturingHotkey = true;
        var etiquetaAntes = view.HotkeyLabel;

        view.OfferKey(HotkeyModifiers.Control, 0x11); // VK_CONTROL held on its own

        Assert.True(view.IsCapturingHotkey);
        Assert.Equal(etiquetaAntes, view.HotkeyLabel);
        Assert.Contains("Ctrl", view.HotkeyHint);
    }

    [Fact]
    public async Task Escape_cancela_la_captura_y_conserva_el_atajo_anterior()
    {
        var view = await BuildAsync(HotkeyBinding.Default);
        view.IsCapturingHotkey = true;
        var etiquetaAntes = view.HotkeyLabel;

        view.OfferKey(HotkeyModifiers.None, 0x1B); // Escape

        Assert.False(view.IsCapturingHotkey);
        Assert.Equal(etiquetaAntes, view.HotkeyLabel);
    }

    [Fact]
    public async Task Una_tecla_sin_ningun_modificador_se_rechaza_y_no_committea()
    {
        // RegisterHotKey accepts a bare key; a global bare "A" would steal that letter
        // everywhere, including inside this same capture UI.
        var view = await BuildAsync(HotkeyBinding.Default);
        view.IsCapturingHotkey = true;
        var etiquetaAntes = view.HotkeyLabel;

        view.OfferKey(HotkeyModifiers.None, 0x41); // "A", no modifier held

        Assert.True(view.IsCapturingHotkey);
        Assert.Equal(etiquetaAntes, view.HotkeyLabel);
        Assert.False(string.IsNullOrWhiteSpace(view.HotkeyHint));
    }

    [Fact]
    public async Task Una_tecla_sin_codigo_win32_reconocible_muestra_un_aviso_y_no_committea()
    {
        var view = await BuildAsync(HotkeyBinding.Default);
        view.IsCapturingHotkey = true;

        view.OfferKey(HotkeyModifiers.Control, 0);

        Assert.True(view.IsCapturingHotkey);
        Assert.False(string.IsNullOrWhiteSpace(view.HotkeyHint));
    }

    [Fact]
    public async Task OfferKey_no_hace_nada_si_la_captura_no_esta_abierta()
    {
        // A key event reaching the view model outside capture mode — e.g. the window
        // still forwarding KeyDown after the settings card is closed — must be a no-op.
        var view = await BuildAsync(HotkeyBinding.Default);
        var etiquetaAntes = view.HotkeyLabel;

        view.OfferKey(HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x4B);

        Assert.Equal(etiquetaAntes, view.HotkeyLabel);
    }

    [Fact]
    public async Task HotkeyChangePending_compara_lo_guardado_contra_lo_registrado()
    {
        // This asserted that capturing alone turned the notice on, which promised the
        // user something no restart would honour: a capture that is never saved simply
        // disappears. The notice is about the saved binding, so the state it describes
        // is "on disk, but not registered yet" - built here directly, since actually
        // saving would write config.json and touch the autostart registry key.
        var guardado = new Settings { Modifiers = HotkeyModifiers.Alt | HotkeyModifiers.Shift, VirtualKey = 0x4B };

        var view = await BuildAsync(HotkeyBinding.Default, guardado);

        Assert.NotEqual(guardado.ToBinding(), HotkeyBinding.Default);
        Assert.True(view.HotkeyChangePending);
        Assert.Equal(HotkeyLabels.For(HotkeyBinding.Default), view.ListeningLabel);

        // And once the registered binding catches up, the notice has nothing to say.
        var alDia = await BuildAsync(guardado.ToBinding(), guardado);
        Assert.False(alDia.HotkeyChangePending);
    }

    [Fact]
    public async Task El_encabezado_muestra_el_atajo_registrado_y_no_el_que_se_esta_editando()
    {
        var view = await BuildAsync(HotkeyBinding.Default);
        view.IsCapturingHotkey = true;

        view.OfferKey(HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x4B); // Alt+Shift+K

        Assert.Equal("Alt+Shift+K", view.HotkeyLabel);
        Assert.Equal(HotkeyLabels.For(HotkeyBinding.Default), view.ListeningLabel);
        Assert.Contains(view.ListeningLabel, view.StatusText);
    }

    [Fact]
    public async Task Cerrar_la_configuracion_desarma_la_captura()
    {
        // The armed state used to outlive the card that showed it. Since the window
        // handler marks every key handled while capture is on, that meant typing
        // anywhere did nothing and the next Ctrl+C was committed as the new hotkey.
        var view = await BuildAsync(HotkeyBinding.Default);
        view.IsSettingsOpen = true;
        view.IsCapturingHotkey = true;

        view.IsSettingsOpen = false;

        Assert.False(view.IsCapturingHotkey);
    }

    [Fact]
    public async Task Con_la_captura_desarmada_un_atajo_cualquiera_no_cambia_nada()
    {
        var view = await BuildAsync(HotkeyBinding.Default);
        view.IsSettingsOpen = true;
        view.IsCapturingHotkey = true;
        view.IsSettingsOpen = false;

        view.OfferKey(HotkeyModifiers.Control, 0x43); // Ctrl+C, meant for copying

        Assert.Equal(HotkeyLabels.For(HotkeyBinding.Default), view.HotkeyLabel);
    }

    [Fact]
    public async Task El_aviso_de_reinicio_no_aparece_solo_por_capturar_sin_guardar()
    {
        // Capturing changes nothing on disk, so promising it applies on the next
        // launch would be a promise a restart silently breaks.
        var view = await BuildAsync(HotkeyBinding.Default);
        view.IsCapturingHotkey = true;

        view.OfferKey(HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x4B); // Alt+Shift+K

        Assert.Equal("Alt+Shift+K", view.HotkeyLabel);
        Assert.False(view.HotkeyChangePending);
    }

    [Fact]
    public async Task Sin_atajo_registrado_todavia_el_aviso_de_reinicio_no_aparece()
    {
        // RegisteredHotkey is null for the whole of Loading, and a record compared
        // against null is always unequal, so without the startup fallback the notice
        // showed up on a launch where the user had changed nothing at all.
        var pipeline = new DictationPipeline(
            Substitute.For<IHotkeyService>(),
            Substitute.For<IAudioCapture>(),
            Substitute.For<ITranscriber>(),
            Substitute.For<ITextInjector>(),
            Substitute.For<IForegroundWindow>(),
            Substitute.For<INoteRepository>(),
            new NullPostProcessor(),
            NullLogger<DictationPipeline>.Instance);

        var view = new MainViewModel(
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

        Assert.Null(pipeline.RegisteredHotkey);
        Assert.False(view.HotkeyChangePending);
    }
}

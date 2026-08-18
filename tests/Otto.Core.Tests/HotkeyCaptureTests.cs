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
    /// <param name="availability">
    /// Defaults to reporting every binding available. NSubstitute returns <c>false</c>
    /// for an unconfigured <c>bool</c> member, so leaving this unconfigured by default
    /// would make every capture test in this file report a phantom conflict.
    /// </param>
    private static async Task<MainViewModel> BuildAsync(
        HotkeyBinding registered,
        Settings? stored = null,
        IHotkeyAvailability? availability = null)
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

        var probe = availability ?? AlwaysAvailable();

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
            },
            probe);
    }

    private static IHotkeyAvailability AlwaysAvailable()
    {
        var availability = Substitute.For<IHotkeyAvailability>();
        availability.IsAvailable(Arg.Any<HotkeyBinding>()).Returns(true);
        return availability;
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
    public async Task Cerrar_la_configuracion_despues_de_capturar_sin_guardar_descarta_lo_capturado()
    {
        // CancelHotkeyCapture used to clear IsCapturingHotkey/HotkeyHint but leave
        // Captured holding whatever had just been committed, and ApplyTo writes
        // Captured unconditionally — so a combination the user backed out of would
        // have been persisted by the next unrelated Guardar (a language change, an
        // autostart toggle), not just by saving the hotkey itself.
        var view = await BuildAsync(HotkeyBinding.Default);
        var etiquetaAntes = view.HotkeyLabel;

        view.IsSettingsOpen = true;
        view.IsCapturingHotkey = true;
        view.OfferKey(HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x4B); // Alt+Shift+K, commits
        Assert.Equal("Alt+Shift+K", view.HotkeyLabel);

        view.IsSettingsOpen = false; // closes the card without ever hitting Guardar

        Assert.Equal(etiquetaAntes, view.HotkeyLabel);
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
            },
            AlwaysAvailable());

        Assert.Null(pipeline.RegisteredHotkey);
        Assert.False(view.HotkeyChangePending);
    }

    [Fact]
    public async Task Una_combinacion_en_uso_por_otra_app_se_rechaza_y_no_committea()
    {
        var availability = Substitute.For<IHotkeyAvailability>();
        availability.IsAvailable(Arg.Any<HotkeyBinding>()).Returns(false);

        var view = await BuildAsync(HotkeyBinding.Default, availability: availability);
        view.IsCapturingHotkey = true;
        var etiquetaAntes = view.HotkeyLabel;

        view.OfferKey(HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x4B); // Alt+Shift+K, owned elsewhere

        Assert.True(view.IsCapturingHotkey);
        Assert.Equal(etiquetaAntes, view.HotkeyLabel);
        Assert.Contains("ya lo está usando", view.HotkeyHint);
    }

    [Fact]
    public async Task Capturar_el_atajo_ya_registrado_por_Otto_no_se_marca_como_conflicto_consigo_mismo()
    {
        // Otto owns pipeline.RegisteredHotkey already, so probing that exact
        // binding would report a false conflict against itself. Asserting the
        // probe is never called — not just that the capture succeeds — is what
        // actually catches a regression to comparing against the stored setting
        // instead, since a probe stubbed to always return true would let that
        // mistake pass silently.
        //
        // `stored` is deliberately a DIFFERENT binding from `registered`. With
        // both equal to HotkeyBinding.Default (the old fixture), a regression to
        // comparing against the stored setting would still pass this test, since
        // candidate, registered and stored would all be the same value — the
        // DidNotReceive assertion would hold either way. Here, a regression to
        // the stored comparison would find candidate (Ctrl+Alt+Espacio) != stored
        // (Alt+Shift+K), so the probe WOULD be called and the test would fail.
        var availability = Substitute.For<IHotkeyAvailability>();
        availability.IsAvailable(Arg.Any<HotkeyBinding>()).Returns(true);
        var stored = new Settings { Modifiers = HotkeyModifiers.Alt | HotkeyModifiers.Shift, VirtualKey = 0x4B };

        var view = await BuildAsync(HotkeyBinding.Default, stored, availability);
        view.IsCapturingHotkey = true;

        view.OfferKey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x20); // same as HotkeyBinding.Default

        Assert.False(view.IsCapturingHotkey);
        Assert.Equal(HotkeyLabels.For(HotkeyBinding.Default), view.HotkeyLabel);
        availability.DidNotReceive().IsAvailable(Arg.Any<HotkeyBinding>());
    }

    [Fact]
    public async Task Mientras_el_pipeline_todavia_carga_el_atajo_de_arranque_no_se_prueba()
    {
        // RegisteredHotkey is null for the whole model-load window, before
        // StartAsync has even attempted Register — and startupHotkey is exactly
        // the binding it is about to register. Probing it here would race Otto's
        // own pending registration and could make Otto blame "another
        // application" for a collision it caused itself, so self-conflict widens
        // to startupHotkey while still loading. HasHotkeyAlert is false here
        // because no registration has been attempted yet, which is what tells
        // this case apart from a real failure (next test).
        var availability = Substitute.For<IHotkeyAvailability>();
        availability.IsAvailable(Arg.Any<HotkeyBinding>()).Returns(true);

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
            new Settings(), // default binding == HotkeyBinding.Default == startupHotkey here
            databasePath: "",
            clipboard: () => null,
            provisioningOptions: new ProvisioningOptions
            {
                ModelsDirectory = "", SpeechFileName = "", VadFileName = "", Label = "", Size = "",
            },
            availability);

        Assert.Null(pipeline.RegisteredHotkey);
        Assert.False(view.HasHotkeyAlert);
        view.IsCapturingHotkey = true;

        view.OfferKey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x20); // same as HotkeyBinding.Default

        Assert.False(view.IsCapturingHotkey);
        availability.DidNotReceive().IsAvailable(Arg.Any<HotkeyBinding>());
    }

    [Fact]
    public async Task Despues_de_un_fallo_de_registro_el_mismo_atajo_si_se_prueba()
    {
        // RegisteredHotkey is null here too, but for a different reason: Register
        // was actually attempted and failed. startupHotkey is precisely the
        // binding that just failed, so skipping the probe for it here — the same
        // rule that is correct while still loading — would hide a real conflict
        // from the user at exactly the moment they need to see it. HasHotkeyAlert
        // is what tells the two null cases apart.
        var availability = Substitute.For<IHotkeyAvailability>();
        availability.IsAvailable(Arg.Any<HotkeyBinding>()).Returns(true);

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
            new Settings(), // default binding == HotkeyBinding.Default == startupHotkey here
            databasePath: "",
            clipboard: () => null,
            provisioningOptions: new ProvisioningOptions
            {
                ModelsDirectory = "", SpeechFileName = "", VadFileName = "", Label = "", Size = "",
            },
            availability);

        view.ShowHotkeyFailure(alreadyInUse: true); // what App's catch does after a failed Register

        Assert.Null(pipeline.RegisteredHotkey);
        Assert.True(view.HasHotkeyAlert);
        view.IsCapturingHotkey = true;

        view.OfferKey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x20); // same as HotkeyBinding.Default, the binding that just failed

        Assert.False(view.IsCapturingHotkey);
        availability.Received(1).IsAvailable(Arg.Any<HotkeyBinding>());
    }
}

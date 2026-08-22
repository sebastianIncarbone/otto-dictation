using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Otto.App;
using Otto.App.ViewModels;
using Otto.Core;
using Otto.Speech;

namespace Otto.Core.Tests;

/// <summary>
/// The settings window shows some of the settings, and the tray menu writes to the
/// same file. That makes "what happens to the fields nobody is looking at" the
/// question worth pinning down.
/// </summary>
public class SettingsTests
{
    private static MainViewModel Build(Settings settings, bool hasGpu = true)
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

        // Alt+Shift+K in Guardar_aplica_el_binding_capturado_por_el_usuario... is a
        // real OfferKey probe call: pipeline.RegisteredHotkey is null here (StartAsync
        // is never called in this file), so an unconfigured substitute — false by
        // NSubstitute's default — would refuse that capture and break the test.
        var availability = Substitute.For<IHotkeyAvailability>();
        availability.IsAvailable(Arg.Any<HotkeyBinding>()).Returns(true);

        return new MainViewModel(
            Substitute.For<INoteRepository>(),
            pipeline,
            new SettingsStore(Path.Combine(Path.GetTempPath(), "otto-tests-no-escribe.json")),
            settings,
            databasePath: "",
            clipboard: () => null,
            provisioningOptions: new ProvisioningOptions
            {
                ModelsDirectory = "", SpeechFileName = "", VadFileName = "", Label = "", Size = "", HasGpu = hasGpu,
            },
            availability);
    }

    [Fact]
    public void Guardar_conserva_lo_que_la_ventana_no_muestra()
    {
        // The bug this pins down: rebuilding the record from the fields on screen
        // reset the hotkey to Ctrl+Alt+Space the first time anyone saved, and it
        // stayed invisible only because the default happened to match.
        var stored = new Settings
        {
            Modifiers = HotkeyModifiers.Shift | HotkeyModifiers.Windows,
            VirtualKey = 0x41,
            Model = "medium",
            CorrectVoseo = false,
        };

        var saved = Build(stored).ApplyTo(stored);

        Assert.Equal(stored.Modifiers, saved.Modifiers);
        Assert.Equal(stored.VirtualKey, saved.VirtualKey);
        Assert.Equal("medium", saved.Model);
        Assert.False(saved.CorrectVoseo);
    }

    [Fact]
    public void Guardar_escribe_lo_que_la_ventana_si_muestra()
    {
        var view = Build(new Settings());

        view.Language = "en";
        view.ShowCharacter = false;
        view.CheckForUpdates = true;

        var saved = view.ApplyTo(new Settings());

        Assert.Equal("en", saved.Language);
        Assert.False(saved.ShowCharacter);
        Assert.True(saved.CheckForUpdates);
    }

    [Fact]
    public void El_personaje_apagado_desde_la_bandeja_sobrevive_a_guardar()
    {
        // The tray toggle and the settings window own the same switch. If the
        // window kept its startup value, saving anything else would turn the
        // character back on behind the user.
        var view = Build(new Settings { ShowCharacter = true });

        view.ReflectCharacterVisibility(false);

        Assert.False(view.ApplyTo(new Settings { ShowCharacter = true }).ShowCharacter);
    }

    [Fact]
    public void Guardar_escribe_la_correccion_y_el_intervalo_de_inactividad()
    {
        var view = Build(new Settings());

        view.CorrectVoseo = false;
        view.CorrectionIdleUnloadMinutes = 30;

        var saved = view.ApplyTo(new Settings());

        Assert.False(saved.CorrectVoseo);
        Assert.Equal(30, saved.CorrectionIdleUnloadMinutes);
    }

    [Fact]
    public void La_correccion_apagada_desde_la_bandeja_sobrevive_a_guardar()
    {
        // Same two-owner shape as El_personaje_apagado_desde_la_bandeja_sobrevive_a_guardar:
        // the tray toggle and the settings window own the same switch, and
        // ReflectCorrectVoseo is what keeps the window's checkbox from going
        // stale without a second writer to the settings file.
        var view = Build(new Settings { CorrectVoseo = true });

        view.ReflectCorrectVoseo(false);

        Assert.False(view.ApplyTo(new Settings { CorrectVoseo = true }).CorrectVoseo);
    }

    [Fact]
    public void La_seccion_de_correccion_se_muestra_con_GPU()
    {
        var view = Build(new Settings(), hasGpu: true);

        Assert.True(view.ShowCorrectionSection);
    }

    /// <summary>
    /// Mirrors the tray's own "hide what it can't do" treatment for CPU-only
    /// hardware (see CorrectionTrayStates.Unsupported) — a checkbox for a
    /// feature that can never load inside the 2s dictation budget on this
    /// machine is worse than no checkbox at all.
    /// </summary>
    [Fact]
    public void La_seccion_de_correccion_se_oculta_sin_GPU()
    {
        var view = Build(new Settings(), hasGpu: false);

        Assert.False(view.ShowCorrectionSection);
    }

    [Fact]
    public void La_etiqueta_por_defecto_coincide_con_el_binding_por_defecto()
    {
        // Regression guard for the defect class itself: the default HotkeyLabel used to
        // be a hardcoded string sitting next to the default Modifiers/VirtualKey, free to
        // drift out of sync with them. Deriving it from the same binding makes that
        // impossible instead of merely unlikely.
        var defaults = new Settings();

        Assert.Equal(HotkeyLabels.For(defaults.ToBinding()), defaults.HotkeyLabel);
    }

    [Fact]
    public void Guardar_aplica_el_binding_capturado_por_el_usuario_no_solo_la_etiqueta()
    {
        // The bug this pins down: the old TextBox let ApplyTo persist a typed label
        // while never writing Modifiers/VirtualKey, so Otto displayed one hotkey and
        // kept listening on another. Capturing has to flow all the way to the saved
        // binding, not just to the text on screen.
        var stored = new Settings();
        var view = Build(stored);

        view.IsCapturingHotkey = true;
        view.OfferKey(HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x4B); // Alt+Shift+K

        var saved = view.ApplyTo(stored);
        var captured = new HotkeyBinding(HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x4B);

        Assert.Equal(captured.Modifiers, saved.Modifiers);
        Assert.Equal(captured.VirtualKey, saved.VirtualKey);
        Assert.Equal(HotkeyLabels.For(captured), saved.HotkeyLabel);
    }

    [Fact]
    public void Un_config_json_viejo_con_PostProcessingModel_sigue_deserializando_y_el_campo_desaparece_al_guardar()
    {
        // PostProcessingModel was an Ollama tag string ("qwen2.5:3b") with no
        // local meaning once correction moved in-process, and was deleted from
        // Settings. System.Text.Json ignores unknown members by default, so a
        // config.json written by an older Otto still has to load — refusing to
        // start on an upgrade would be exactly the kind of silent breakage the
        // "amended, never rebuilt" convention exists to prevent — and the field
        // has to actually disappear the next time Otto writes the file, since
        // Settings no longer has anywhere to put a value for it.
        var path = Path.Combine(Path.GetTempPath(), $"otto-settings-viejo-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            {
              "Language": "es",
              "Model": "large-v3-turbo",
              "CorrectVoseo": true,
              "PostProcessingModel": "qwen2.5:3b"
            }
            """);

        try
        {
            var store = new SettingsStore(path);
            var loaded = store.Load();

            Assert.Equal("es", loaded.Language);
            Assert.Equal("large-v3-turbo", loaded.Model);
            Assert.True(loaded.CorrectVoseo);

            store.Save(loaded);

            Assert.DoesNotContain("PostProcessingModel", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Un_archivo_ilegible_no_impide_arrancar()
    {
        var path = Path.Combine(Path.GetTempPath(), $"otto-roto-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ esto no es json");

        try
        {
            // Refusing to start would leave the user with no way to fix it from
            // inside Otto.
            Assert.Equal(new Settings().Language, new SettingsStore(path).Load().Language);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

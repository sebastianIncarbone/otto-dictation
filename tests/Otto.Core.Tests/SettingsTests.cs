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
    private static MainViewModel Build(Settings settings)
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

        return new MainViewModel(
            Substitute.For<INoteRepository>(),
            pipeline,
            new SettingsStore(Path.Combine(Path.GetTempPath(), "otto-tests-no-escribe.json")),
            settings,
            databasePath: "",
            clipboard: () => null,
            provisioningOptions: new ProvisioningOptions
            {
                ModelsDirectory = "", SpeechFileName = "", VadFileName = "", Label = "", Size = "",
            });
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
            PostProcessingModel = "llama3.2:1b",
        };

        var saved = Build(stored).ApplyTo(stored);

        Assert.Equal(stored.Modifiers, saved.Modifiers);
        Assert.Equal(stored.VirtualKey, saved.VirtualKey);
        Assert.Equal("medium", saved.Model);
        Assert.Equal("llama3.2:1b", saved.PostProcessingModel);
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

using Avalonia.Media;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Otto.App;
using Otto.App.ViewModels;
using Otto.App.Views;
using Otto.Core;
using Otto.Speech;

namespace Otto.Core.Tests;

/// <summary>
/// The minimal overlay — a dot and three lines instead of the character.
///
/// <para>
/// Two kinds of thing are pinned here. The first is the design contract:
/// <see cref="OttoGlyph"/>'s colours and bar widths come from a design file this
/// repository does not contain, so nothing but a test stops them drifting into
/// "close enough" during an unrelated edit. They are public on the control for
/// exactly that reason.
/// </para>
/// <para>
/// The second is the settings round trip, which is where the equivalent hotkey
/// feature failed: a value the interface displayed and the file never carried. A
/// default that reads back as something else would silently change the overlay of
/// every existing install on upgrade.
/// </para>
/// </summary>
public class CharacterAppearanceTests
{
    // ---- The design contract ----

    [Theory]
    [InlineData(DictationState.Recording, 0xFF, 0x24, 0x38)]
    [InlineData(DictationState.Transcribing, 0xD9, 0x7A, 0x08)]
    [InlineData(DictationState.Loading, 0x7A, 0x82, 0x94)]
    public void El_glifo_usa_los_colores_exactos_del_diseño(DictationState state, byte r, byte g, byte b)
    {
        var (colour, opacity) = OttoGlyph.Palette(state);

        Assert.Equal(Color.FromRgb(r, g, b), colour);
        Assert.Equal(1.0, opacity);
    }

    /// <summary>
    /// Idle is the one state the design gives as a translucent white rather than a
    /// solid colour, and the one whose opacity therefore carries meaning.
    /// </summary>
    [Fact]
    public void En_reposo_el_glifo_es_blanco_a_la_mitad()
    {
        var (colour, opacity) = OttoGlyph.Palette(DictationState.Idle);

        Assert.Equal(Colors.White, colour);
        Assert.Equal(0.5, opacity);
    }

    /// <summary>
    /// Growth is the only change the design makes to the shapes themselves, and it
    /// belongs to listening alone. If another state started widening them, the one
    /// signal that survives being glanced at would stop meaning "it is hearing you".
    /// </summary>
    [Fact]
    public void Solo_escuchando_ensancha_las_barras()
    {
        Assert.Equal([18d, 26d, 12d], OttoGlyph.BarWidths(DictationState.Recording));

        foreach (var quiet in new[] { DictationState.Idle, DictationState.Loading, DictationState.Transcribing })
            Assert.Equal([14d, 20d, 10d], OttoGlyph.BarWidths(quiet));
    }

    /// <summary>The glyph's canvas is the design's, and the window is sized from it.</summary>
    [Fact]
    public void El_glifo_conserva_el_lienzo_del_diseño()
    {
        Assert.Equal(64d, OttoGlyph.DesignWidth);
        Assert.Equal(24d, OttoGlyph.DesignHeight);
    }

    // ---- The settings round trip ----

    [Fact]
    public void La_apariencia_por_defecto_es_el_personaje()
        => Assert.Equal(CharacterAppearance.Character, new Settings().CharacterAppearance);

    /// <summary>
    /// The case that decides what happens to everyone who already has Otto
    /// installed: their settings file predates this field entirely, and reading it
    /// must leave them with the overlay they had rather than switching them to a
    /// new one they never asked for.
    /// </summary>
    [Fact]
    public void Un_archivo_sin_el_campo_conserva_el_personaje()
    {
        var path = TempFile();

        try
        {
            File.WriteAllText(path, """{ "Language": "es", "ShowCharacter": true }""");

            Assert.Equal(CharacterAppearance.Character, new SettingsStore(path).Load().CharacterAppearance);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The settings file is documented as something its owner can read and edit by
    /// hand, so the enum is written as its name and not as the integer
    /// System.Text.Json would otherwise choose.
    /// </summary>
    [Fact]
    public void La_apariencia_se_guarda_como_texto_y_no_como_numero()
    {
        var path = TempFile();

        try
        {
            var store = new SettingsStore(path);
            store.Save(new Settings { CharacterAppearance = CharacterAppearance.Minimal });

            var written = File.ReadAllText(path);

            Assert.Contains("\"Minimal\"", written);
            Assert.Equal(CharacterAppearance.Minimal, store.Load().CharacterAppearance);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- The choice in the settings window ----

    /// <summary>
    /// The pair of booleans the radio group binds to are views onto one enum, so
    /// the states that have no meaning — neither checked, both checked — cannot be
    /// reached from the interface.
    /// </summary>
    [Fact]
    public void Los_dos_botones_son_vistas_de_un_solo_valor()
    {
        var view = Build();

        Assert.True(view.IsCharacterAppearance);
        Assert.False(view.IsMinimalAppearance);

        view.IsMinimalAppearance = true;

        Assert.Equal(CharacterAppearance.Minimal, view.Appearance);
        Assert.True(view.IsMinimalAppearance);
        Assert.False(view.IsCharacterAppearance);
    }

    /// <summary>
    /// Unchecking is not a thing a radio group does — the other option's check is
    /// what turns this one off. Setting false has to be inert, or the group would
    /// be able to land on "neither".
    /// </summary>
    [Fact]
    public void Desmarcar_un_boton_no_apaga_la_eleccion()
    {
        var view = Build();
        view.IsMinimalAppearance = true;

        view.IsMinimalAppearance = false;

        Assert.Equal(CharacterAppearance.Minimal, view.Appearance);
    }

    /// <summary>
    /// Both booleans have to be raised when the enum moves, or the radio the user
    /// did not click keeps its old check and the group shows two.
    /// </summary>
    [Fact]
    public void Cambiar_la_apariencia_avisa_por_los_dos_botones()
    {
        var view = Build();
        var raised = new List<string>();

        view.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        view.Appearance = CharacterAppearance.Minimal;

        Assert.Contains(nameof(MainViewModel.IsCharacterAppearance), raised);
        Assert.Contains(nameof(MainViewModel.IsMinimalAppearance), raised);
    }

    /// <summary>
    /// Amended, never rebuilt. The window does not show the model names, so a fresh
    /// record would reset them — the bug that quietly reset the hotkey binding on
    /// every save and stayed invisible only because the default matched.
    /// </summary>
    [Fact]
    public void Elegir_el_punto_minimo_no_pisa_el_resto_de_los_ajustes()
    {
        var view = Build();
        view.IsMinimalAppearance = true;

        var applied = view.ApplyTo(new Settings { PostProcessingModel = "qwen2.5:7b", Model = "base" });

        Assert.Equal(CharacterAppearance.Minimal, applied.CharacterAppearance);
        Assert.Equal("qwen2.5:7b", applied.PostProcessingModel);
        Assert.Equal("base", applied.Model);
    }

    /// <summary>
    /// Showing the overlay and choosing which overlay it is are separate
    /// preferences: turning the character off must not decide the appearance, and
    /// picking the minimal one must not turn anything on.
    /// </summary>
    [Fact]
    public void Esconder_el_personaje_no_cambia_la_apariencia_elegida()
    {
        var view = Build(new Settings { CharacterAppearance = CharacterAppearance.Minimal });

        view.ShowCharacter = false;

        var applied = view.ApplyTo(new Settings());

        Assert.False(applied.ShowCharacter);
        Assert.Equal(CharacterAppearance.Minimal, applied.CharacterAppearance);
    }

    // ---- Doubles ----

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"otto-apariencia-{Guid.NewGuid():N}.json");

    private static MainViewModel Build(Settings? stored = null)
    {
        var availability = Substitute.For<IHotkeyAvailability>();
        availability.IsAvailable(Arg.Any<HotkeyBinding>()).Returns(true);

        return new(
            Substitute.For<INoteRepository>(),
            BuildPipeline(),
            new SettingsStore(Path.Combine(Path.GetTempPath(), "otto-tests-no-escribe.json")),
            stored ?? new Settings(),
            databasePath: "",
            clipboard: () => null,
            provisioningOptions: new ProvisioningOptions
            {
                ModelsDirectory = "", SpeechFileName = "", VadFileName = "", Label = "", Size = "",
            },
            availability);
    }

    private static DictationPipeline BuildPipeline() => new(
        Substitute.For<IHotkeyService>(),
        Substitute.For<IAudioCapture>(),
        Substitute.For<ITranscriber>(),
        Substitute.For<ITextInjector>(),
        Substitute.For<IForegroundWindow>(),
        Substitute.For<INoteRepository>(),
        new NullPostProcessor(),
        NullLogger<DictationPipeline>.Instance);
}

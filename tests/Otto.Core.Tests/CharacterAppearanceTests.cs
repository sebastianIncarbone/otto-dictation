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
/// The two quieter overlays — the discreet ring and the minimal glyph — and the
/// three-way choice between them and the character.
///
/// <para>
/// Two kinds of thing are pinned here. The first is the design contract:
/// <see cref="OttoRing"/>'s and <see cref="OttoGlyph"/>'s colours and geometry come
/// from a design file this repository does not contain, so nothing but a test stops
/// them drifting into "close enough" during an unrelated edit. They are public on
/// the controls for exactly that reason.
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

    // ---- The discreet ring, section F17 ----

    [Theory]
    [InlineData(DictationState.Recording, 0xFF, 0x24, 0x38, 1.00, 6.0, 0.38)]
    [InlineData(DictationState.Transcribing, 0xD9, 0x7A, 0x08, 1.00, 5.5, 0.34)]
    public void El_anillo_usa_los_colores_exactos_del_diseño(
        DictationState state, byte r, byte g, byte b, double strokeOpacity, double strokeWidth, double fillOpacity)
    {
        var spec = OttoRing.Spec(state);

        Assert.Equal(Color.FromRgb(r, g, b), spec.Stroke);
        Assert.Equal(strokeOpacity, spec.StrokeOpacity);
        Assert.Equal(strokeWidth, spec.StrokeWidth);
        Assert.Equal(fillOpacity, spec.FillOpacity);
    }

    /// <summary>
    /// The two quiet states are white rings that differ only in how much of them
    /// there is — which is the whole distinction between "waiting for you" and
    /// "cannot help you yet", so collapsing them would erase it.
    /// </summary>
    [Fact]
    public void El_anillo_en_reposo_y_cargando_se_distinguen_solo_por_la_opacidad()
    {
        var idle = OttoRing.Spec(DictationState.Idle);
        var loading = OttoRing.Spec(DictationState.Loading);

        Assert.Equal(Colors.White, idle.Stroke);
        Assert.Equal(Colors.White, loading.Stroke);

        Assert.Equal(0.55, idle.StrokeOpacity);
        Assert.Equal(0.22, loading.StrokeOpacity);
    }

    /// <summary>
    /// The disc behind the ring is the same near-black in every state. Only how
    /// much desktop shows through it changes, so a state can never be read from the
    /// fill colour by mistake.
    /// </summary>
    [Fact]
    public void El_disco_del_anillo_es_el_mismo_negro_en_todos_los_estados()
    {
        var fills = new[]
        {
            DictationState.Idle, DictationState.Loading,
            DictationState.Recording, DictationState.Transcribing,
        }.Select(state => OttoRing.Spec(state).Fill).Distinct();

        Assert.Single(fills);
    }

    /// <summary>
    /// Listening is the only state that grows the bars, and it grows all three.
    /// Everything else shows them at rest — the design's shape for "there is an
    /// overlay here", not a reading of anything.
    /// </summary>
    [Fact]
    public void Solo_escuchando_agranda_las_barras_del_anillo()
    {
        Assert.Equal(
            [(61.5, 7d, 22d), (72d, 7d, 38d), (82.5, 7d, 14d)],
            OttoRing.Bars(DictationState.Recording));

        foreach (var quiet in new[] { DictationState.Idle, DictationState.Loading, DictationState.Transcribing })
            Assert.Equal([(62d, 6d, 10d), (72d, 6d, 16d), (82d, 6d, 8d)], OttoRing.Bars(quiet));
    }

    /// <summary>
    /// The canvas is the design's and does not move; the window is 40% smaller than
    /// it.
    ///
    /// Two constants because they mean two different things. Every coordinate in
    /// <see cref="OttoRing"/> is in canvas units, so conflating them would turn
    /// "make the overlay smaller" from a one-number edit into a forty-number one —
    /// and forty numbers that no longer match the design file are forty chances to
    /// get one wrong.
    /// </summary>
    [Fact]
    public void El_anillo_se_dibuja_en_144_y_se_muestra_al_60_por_ciento()
    {
        Assert.Equal(144d, OttoRing.DesignSize);
        Assert.Equal(0.6, OttoRing.WindowSize / OttoRing.DesignSize, precision: 10);
    }

    /// <summary>
    /// The three appearances are three footprints, in the order they ask for
    /// attention. A test rather than a comment because the window sizes itself from
    /// these, and two of them becoming equal is the kind of thing a later edit does
    /// by accident.
    /// </summary>
    [Fact]
    public void Cada_apariencia_ocupa_menos_pantalla_que_la_anterior()
    {
        // The character's side. CharacterWindow keeps its own copy private, and it
        // is the canvas both 144-unit designs were drawn on.
        const double character = 144;

        Assert.True(OttoRing.WindowSize < character);
        Assert.True(OttoGlyph.DesignWidth < OttoRing.WindowSize);
        Assert.True(OttoGlyph.DesignHeight < OttoRing.WindowSize);
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
    [Theory]
    [InlineData(CharacterAppearance.Discreet, "Discreet")]
    [InlineData(CharacterAppearance.Minimal, "Minimal")]
    public void La_apariencia_se_guarda_como_texto_y_no_como_numero(
        CharacterAppearance appearance, string expected)
    {
        var path = TempFile();

        try
        {
            var store = new SettingsStore(path);
            store.Save(new Settings { CharacterAppearance = appearance });

            var written = File.ReadAllText(path);

            Assert.Contains($"\"{expected}\"", written);
            Assert.Equal(appearance, store.Load().CharacterAppearance);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- The choice in the settings window ----

    /// <summary>
    /// The booleans the radio group binds to are views onto one enum, so the states
    /// that have no meaning — none checked, more than one checked — cannot be
    /// reached from the interface.
    /// </summary>
    [Fact]
    public void Los_botones_son_vistas_de_un_solo_valor()
    {
        var view = Build();

        Assert.True(view.IsCharacterAppearance);
        Assert.False(view.IsDiscreetAppearance);
        Assert.False(view.IsMinimalAppearance);

        view.IsMinimalAppearance = true;

        Assert.Equal(CharacterAppearance.Minimal, view.Appearance);
        Assert.True(view.IsMinimalAppearance);
        Assert.False(view.IsCharacterAppearance);
        Assert.False(view.IsDiscreetAppearance);
    }

    /// <summary>
    /// Whichever option is chosen, it is the only one checked. Written as a sweep
    /// over all three rather than as one case, because the failure this catches —
    /// an option that forgets to turn another off — only appears for the specific
    /// pair that was missed.
    /// </summary>
    [Theory]
    [InlineData(CharacterAppearance.Character)]
    [InlineData(CharacterAppearance.Discreet)]
    [InlineData(CharacterAppearance.Minimal)]
    public void Exactamente_una_apariencia_queda_marcada(CharacterAppearance chosen)
    {
        var view = Build();

        view.Appearance = chosen;

        var checkedOptions = new[]
        {
            view.IsCharacterAppearance,
            view.IsDiscreetAppearance,
            view.IsMinimalAppearance,
        };

        Assert.Single(checkedOptions, isChecked => isChecked);
    }

    /// <summary>
    /// The middle option, which is the one the settings window gained last and the
    /// one a three-way choice is easiest to drop on the floor.
    /// </summary>
    [Fact]
    public void Elegir_discreto_llega_hasta_los_ajustes()
    {
        var view = Build();

        view.IsDiscreetAppearance = true;

        Assert.Equal(CharacterAppearance.Discreet, view.Appearance);
        Assert.Equal(CharacterAppearance.Discreet, view.ApplyTo(new Settings()).CharacterAppearance);
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
    /// Amended, never rebuilt. The window does not show the model name or the
    /// correction toggle, so a fresh record would reset them — the bug that
    /// quietly reset the hotkey binding on every save and stayed invisible only
    /// because the default matched.
    /// </summary>
    [Fact]
    public void Elegir_el_punto_minimo_no_pisa_el_resto_de_los_ajustes()
    {
        // CorrectVoseo is deliberately not part of this example anymore: it
        // is now a field the window shows and ApplyTo writes from (like
        // Language), the same as Appearance itself — Model is what stands
        // in for "not shown in the window, must pass through untouched".
        var view = Build();
        view.IsMinimalAppearance = true;

        var applied = view.ApplyTo(new Settings { Model = "base" });

        Assert.Equal(CharacterAppearance.Minimal, applied.CharacterAppearance);
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

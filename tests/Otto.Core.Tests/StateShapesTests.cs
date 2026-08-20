using Avalonia.Media;
using Otto.App;
using Otto.Core;

namespace Otto.Core.Tests;

/// <summary>
/// The state motif shared by the tray icon and the window header.
///
/// <para>
/// The claim these tests exist to protect is the redesign's, and it is an
/// accessibility one: a state is told by SHAPE, not only by colour. Four coloured
/// circles are one icon to anyone with a colour vision deficiency, and one icon at
/// a glance to everyone else, because a 16 px mark in the notification area is read
/// by its outline before its hue. Nothing in the type system stops a later edit
/// from quietly making two states the same figure and leaving only the colour to
/// tell them apart.
/// </para>
/// </summary>
public class StateShapesTests
{
    private static readonly DictationState[] All =
    [
        DictationState.Idle,
        DictationState.Loading,
        DictationState.Recording,
        DictationState.Transcribing,
    ];

    /// <summary>The whole point: four states, four figures.</summary>
    [Fact]
    public void Cada_estado_es_una_figura_distinta()
    {
        var kinds = All.Select(state => StateShapes.For(state).Kind).ToArray();

        Assert.Equal(kinds.Length, kinds.Distinct().Count());
    }

    /// <summary>And four colours, so the two signals agree instead of competing.</summary>
    [Fact]
    public void Cada_estado_tiene_su_propio_color()
    {
        var colours = All.Select(state => StateShapes.For(state).Colour).ToArray();

        Assert.Equal(colours.Length, colours.Distinct().Count());
    }

    [Theory]
    [InlineData(DictationState.Idle, 0x1E, 0x9E, 0x5A)]
    [InlineData(DictationState.Loading, 0xA6, 0xAC, 0xBA)]
    [InlineData(DictationState.Recording, 0xFF, 0x24, 0x38)]
    [InlineData(DictationState.Transcribing, 0xD9, 0x7A, 0x08)]
    public void Los_colores_son_los_del_diseño(DictationState state, byte r, byte g, byte b)
        => Assert.Equal(Color.FromRgb(r, g, b), StateShapes.For(state).Colour);

    /// <summary>
    /// Ready is a closed ring and loading is an open one. This is the pair most at
    /// risk of collapsing into "same shape, different grey", and it is also the pair
    /// that matters most: "ready" and "not ready yet" is the distinction a user acts
    /// on. Swept all the way around rather than sampled at one angle, because a gap
    /// tested at a single point is a gap tested nowhere.
    /// </summary>
    [Fact]
    public void El_anillo_de_listo_es_cerrado_y_el_de_cargando_esta_abierto()
    {
        Assert.True(Sweep(DictationState.Idle).All(covered => covered));

        var loading = Sweep(DictationState.Loading);

        Assert.Contains(true, loading);
        Assert.Contains(false, loading);
    }

    /// <summary>Listening is solid; the two rings are hollow. Told from the centre out.</summary>
    [Fact]
    public void Solo_escuchando_esta_relleno_en_el_centro()
    {
        Assert.Equal(1, Centre(DictationState.Recording));

        Assert.Equal(0, Centre(DictationState.Idle));
        Assert.Equal(0, Centre(DictationState.Loading));
    }

    /// <summary>
    /// The bars are three, with gaps between them — not a block. Sampled inside the
    /// middle bar and in the space beside it.
    /// </summary>
    [Fact]
    public void Procesando_son_tres_barras_separadas()
    {
        var shape = StateShapes.For(DictationState.Transcribing);

        Assert.Equal(3, StateShapes.Bars.Length);

        // Middle of the tallest bar, then the gap immediately to its left.
        Assert.Equal(1, StateShapes.Coverage(shape, 16, 16));
        Assert.Equal(0, StateShapes.Coverage(shape, 12, 16));
    }

    /// <summary>
    /// Every figure stays inside the canvas it is rasterised on. A stroke is centred
    /// on its radius, so a ring's outer edge sits half a thickness beyond it — the
    /// arithmetic that clips an icon if a later edit grows one.
    /// </summary>
    [Fact]
    public void Ninguna_figura_se_sale_del_lienzo()
    {
        foreach (var state in All)
        {
            var shape = StateShapes.For(state);

            var reach = shape.Kind == StateShapeKind.Bars
                ? StateShapes.Bars.Max(bar => Math.Max(bar.X + bar.W, bar.Y + bar.H))
                : StateShapes.Centre + shape.Radius + shape.Thickness / 2;

            Assert.True(reach <= StateShapes.Canvas, $"{state} se sale del lienzo: {reach}");
        }
    }

    [Theory]
    [InlineData(DictationState.Idle, "Otto — listo")]
    [InlineData(DictationState.Loading, "Otto — cargando modelo")]
    [InlineData(DictationState.Recording, "Otto — escuchando")]
    [InlineData(DictationState.Transcribing, "Otto — procesando")]
    public void Los_tooltips_son_los_del_diseño(DictationState state, string expected)
        => Assert.Equal(expected, StateShapes.Tooltip(state));

    // ---- Helpers ----

    /// <summary>Walks the ring's own radius all the way round, one sample per degree.</summary>
    private static bool[] Sweep(DictationState state)
    {
        var shape = StateShapes.For(state);

        return Enumerable.Range(0, 360).Select(degree =>
        {
            var radians = degree * Math.PI / 180;

            var x = StateShapes.Centre + Math.Cos(radians) * shape.Radius;
            var y = StateShapes.Centre + Math.Sin(radians) * shape.Radius;

            return StateShapes.Coverage(shape, x, y) > 0.5;
        }).ToArray();
    }

    private static double Centre(DictationState state)
        => StateShapes.Coverage(StateShapes.For(state), StateShapes.Centre, StateShapes.Centre);
}

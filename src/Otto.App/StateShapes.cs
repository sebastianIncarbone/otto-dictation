using Avalonia.Media;
using Otto.Core;

namespace Otto.App;

/// <summary>Which of the four figures a state is drawn as.</summary>
public enum StateShapeKind
{
    /// <summary>A closed ring. Ready.</summary>
    Ring,

    /// <summary>A ring broken into one long dash. Loading.</summary>
    DashedRing,

    /// <summary>A solid filled circle. Listening.</summary>
    Disc,

    /// <summary>Three bars of unequal height. Transcribing.</summary>
    Bars,
}

/// <summary>
/// One state, as a figure and a colour.
/// </summary>
/// <param name="Kind">The figure. This, not <paramref name="Colour"/>, is what has to carry the state.</param>
/// <param name="Colour">The state colour, from the redesign.</param>
/// <param name="Radius">Circle radius in canvas units; unused by <see cref="StateShapeKind.Bars"/>.</param>
/// <param name="Thickness">Stroke width for the two ring kinds, centred on the radius as SVG strokes are.</param>
/// <param name="DashOn">Length of the drawn arc, in canvas units along the circumference.</param>
/// <param name="DashOff">Length of the gap.</param>
public readonly record struct StateShape(
    StateShapeKind Kind,
    Color Colour,
    double Radius,
    double Thickness,
    double DashOn,
    double DashOff);

/// <summary>
/// The state motif: four figures that say what Otto is doing, drawn the same way
/// in the tray icon and in the window header.
///
/// <para>
/// The redesign's point, and the reason this is one shared source rather than two
/// convenient copies: a state is told by SHAPE, not only by colour. Four coloured
/// circles are one icon to anyone with a colour vision deficiency, and they are
/// also one icon at a glance to everyone else — a 16 px dot in the notification
/// area is read by its outline long before its hue. A ring, a broken ring, a solid
/// dot and three bars are four icons.
/// </para>
/// <para>
/// Everything is in units of a 32-wide canvas, which is the size the tray icon is
/// rasterised at and the size the design file gives its numbers in. The header
/// glyph scales the same figures down; the design drew that one on a 16-unit
/// canvas, and the two agree exactly at ×2 for three of the four states — the
/// loading ring differed by a unit, and the tray's numbers win, because a motif
/// that is "the same" in two places has to actually be the same.
/// </para>
/// </summary>
public static class StateShapes
{
    /// <summary>The canvas every number below is in.</summary>
    public const double Canvas = 32;

    /// <summary>Dead centre of that canvas.</summary>
    public const double Centre = Canvas / 2;

    private static readonly Color Ready = Color.FromRgb(0x1E, 0x9E, 0x5A);
    private static readonly Color Waiting = Color.FromRgb(0xA6, 0xAC, 0xBA);
    private static readonly Color Listening = Color.FromRgb(0xFF, 0x24, 0x38);
    private static readonly Color Working = Color.FromRgb(0xD9, 0x7A, 0x08);

    /// <summary>
    /// The three bars, as (x, y, width, height) on the canvas. Unequal heights on
    /// purpose — a level meter frozen mid-reading, which is what "processing what
    /// you just said" looks like.
    /// </summary>
    public static readonly (double X, double Y, double W, double H)[] Bars =
    [
        (5, 12, 6, 12),
        (13, 6, 6, 20),
        (21, 14, 6, 8),
    ];

    public static StateShape For(DictationState state) => state switch
    {
        // A broken ring rather than a dimmer one: "not ready yet" has to be
        // distinguishable from "ready" without a colour reference to compare against.
        DictationState.Loading      => new(StateShapeKind.DashedRing, Waiting, 11, 5, 46, 24),

        // Solid, and the largest of the four. The one state where the user needs to
        // know at a glance from across the desk.
        DictationState.Recording    => new(StateShapeKind.Disc, Listening, 13, 0, 0, 0),

        DictationState.Transcribing => new(StateShapeKind.Bars, Working, 0, 0, 0, 0),

        _                           => new(StateShapeKind.Ring, Ready, 11, 5, 0, 0),
    };

    /// <summary>
    /// What the tray tooltip says. The design lists all four; the provisioning case
    /// carries its own percentage and is set by whoever owns the download.
    /// </summary>
    public static string Tooltip(DictationState state) => state switch
    {
        DictationState.Loading      => "Otto — cargando modelo",
        DictationState.Recording    => "Otto — escuchando",
        DictationState.Transcribing => "Otto — procesando",
        _                           => "Otto — listo",
    };

    /// <summary>
    /// How much of the pixel at <paramref name="x"/>, <paramref name="y"/> the
    /// figure covers, from 0 to 1.
    ///
    /// One function for both renderers so the two cannot drift: the tray writes the
    /// result straight into a pixel buffer, and the header control uses the same
    /// geometry through Avalonia's own drawing. Half a pixel of feathering at every
    /// edge — without it the shapes read as jagged blobs at 16 px in the
    /// notification area.
    /// </summary>
    public static double Coverage(in StateShape shape, double x, double y)
    {
        if (shape.Kind == StateShapeKind.Bars)
        {
            var most = 0.0;

            foreach (var (bx, by, bw, bh) in Bars)
            {
                var inside = Math.Min(
                    Math.Min(x - bx, bx + bw - x),
                    Math.Min(y - by, by + bh - y));

                most = Math.Max(most, Math.Clamp(inside + 0.5, 0, 1));
            }

            return most;
        }

        var distance = Math.Sqrt((x - Centre) * (x - Centre) + (y - Centre) * (y - Centre));

        if (shape.Kind == StateShapeKind.Disc)
            return Math.Clamp(shape.Radius - distance + 0.5, 0, 1);

        // SVG centres a stroke on its path, so the band straddles the radius.
        var inner = shape.Radius - shape.Thickness / 2;
        var outer = shape.Radius + shape.Thickness / 2;

        var band = Math.Clamp(Math.Min(outer - distance, distance - inner) + 0.5, 0, 1);

        if (band <= 0 || shape.Kind != StateShapeKind.DashedRing) return band;

        // Where this pixel falls along the circumference, measured the way the
        // design's stroke-dasharray is: in canvas units, from twelve o'clock.
        var angle = Math.Atan2(y - Centre, x - Centre) + Math.PI / 2;
        if (angle < 0) angle += Math.PI * 2;

        var along = angle * shape.Radius % (shape.DashOn + shape.DashOff);

        return along < shape.DashOn ? band : 0;
    }
}

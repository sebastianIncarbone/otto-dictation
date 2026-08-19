using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Otto.Core;

namespace Otto.App.Views;

/// <summary>
/// The minimal overlay: a dot and three lines, in place of the character.
///
/// <para>
/// Same job as <see cref="OttoCharacter"/> — answer "is Otto on, and what is it
/// doing" without the user going to look — and a deliberately smaller claim on the
/// screen. The character is a 144 px drawing that moves; this is 64×24 of flat
/// shapes that only change colour. Someone who wants Otto present but not
/// performing gets to have that.
/// </para>
/// <para>
/// Nothing animates except the loading ring, and that is not decoration: the ring
/// is a dashed arc, and a dashed arc holding still reads as a broken circle rather
/// than as work in progress. Every other state is a repaint on change and no timer
/// at all, which is what lets this run all day without appearing in the user's
/// battery or fan noise — the same budget <see cref="OttoCharacter"/> spends 30 fps
/// against, here spent on nothing.
/// </para>
/// </summary>
public sealed class OttoGlyph : Control
{
    public static readonly StyledProperty<DictationState> StateProperty =
        AvaloniaProperty.Register<OttoGlyph, DictationState>(nameof(State));

    public DictationState State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>
    /// The canvas the design was drawn on. Every coordinate below is in these units
    /// and scaled to whatever bounds the control is given, so the glyph can be
    /// resized without re-deriving a single number from the design.
    /// </summary>
    public const double DesignWidth = 64;

    /// <inheritdoc cref="DesignWidth"/>
    public const double DesignHeight = 24;

    private const double DotX = 10;
    private const double DotY = 12;
    private const double DotRadius = 5.5;

    private const double BarX = 22;
    private const double BarHeight = 2.5;
    private const double BarRadius = 1.25;

    /// <summary>Top edge of each of the three lines.</summary>
    private static readonly double[] BarTops = [5, 10.75, 16.5];

    /// <summary>
    /// How wide the three lines are in each state.
    ///
    /// Only <see cref="DictationState.Recording"/> differs, and it is wider on every
    /// line rather than taller or brighter: growth is the one change that survives
    /// being glanced at from across a desk, which is the only way this thing is ever
    /// looked at.
    /// </summary>
    public static double[] BarWidths(DictationState state) => state switch
    {
        DictationState.Recording => [18, 26, 12],
        _                        => [14, 20, 10],
    };

    /// <summary>
    /// The state colours, taken from the redesign.
    ///
    /// Idle is white at 50%, which is the design's own choice and reads well on the
    /// dark desktops it was drawn against. It is the one value here with a known
    /// weakness: over a white document, white at half opacity is close to invisible.
    /// Left exactly as designed rather than quietly corrected — the fix is a design
    /// decision (an outline, a shadow, a different idle colour) and not the
    /// implementation's to make.
    /// </summary>
    public static (Color Colour, double Opacity) Palette(DictationState state) => state switch
    {
        DictationState.Loading      => (Color.FromRgb(0x7A, 0x82, 0x94), 1.0),
        DictationState.Recording    => (Color.FromRgb(0xFF, 0x24, 0x38), 1.0),
        DictationState.Transcribing => (Color.FromRgb(0xD9, 0x7A, 0x08), 1.0),
        _                           => (Colors.White, 0.5),
    };

    /// <summary>
    /// Runs only while loading. Constructed once and started and stopped as the
    /// state moves, so the common case — Otto sitting idle for hours — costs no
    /// timer ticks whatsoever.
    /// </summary>
    private readonly DispatcherTimer timer;

    private double phase;

    public OttoGlyph()
    {
        // ~20 fps. The ring turns slowly and nothing else moves, so the extra
        // smoothness of 30 would be spent on a rotation nobody is watching closely.
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, Tick);
    }

    static OttoGlyph()
    {
        AffectsRender<OttoGlyph>(StateProperty);
        StateProperty.Changed.AddClassHandler<OttoGlyph>((glyph, _) => glyph.SyncTimer());
    }

    private void Tick(object? sender, EventArgs e)
    {
        phase += 0.05;
        InvalidateVisual();
    }

    /// <summary>
    /// Starts the timer when the ring needs to turn and stops it the moment it does
    /// not. Called on every state change and on attach, because a control that is
    /// constructed already loading would otherwise never start it.
    /// </summary>
    private void SyncTimer()
    {
        var wanted = State == DictationState.Loading;

        if (wanted == timer.IsEnabled) return;

        if (wanted) timer.Start();
        else timer.Stop();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SyncTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        // Uniform scale and centred, so the glyph keeps its proportions in a window
        // whose aspect ratio does not match the design's.
        var scale = Math.Min(Bounds.Width / DesignWidth, Bounds.Height / DesignHeight);
        if (scale <= 0) return;

        var transform =
            Matrix.CreateScale(scale, scale) *
            Matrix.CreateTranslation(
                (Bounds.Width - DesignWidth * scale) / 2,
                (Bounds.Height - DesignHeight * scale) / 2);

        using var scaled = context.PushTransform(transform);

        var (colour, opacity) = Palette(State);
        var brush = new SolidColorBrush(colour, opacity);

        DrawDot(context, brush);

        var widths = BarWidths(State);

        for (var i = 0; i < BarTops.Length; i++)
        {
            var bar = new Rect(BarX, BarTops[i], widths[i], BarHeight);

            context.DrawRectangle(brush, null, new RoundedRect(bar, BarRadius));
        }
    }

    /// <summary>
    /// Filled in every state but one. While loading it is an open dashed arc that
    /// turns — the only moving part of the whole overlay, and the only state where
    /// standing still would be a lie about whether anything is happening.
    /// </summary>
    private void DrawDot(DrawingContext context, IBrush brush)
    {
        var centre = new Point(DotX, DotY);

        if (State != DictationState.Loading)
        {
            context.DrawEllipse(brush, null, centre, DotRadius, DotRadius);
            return;
        }

        const double thickness = 2;

        // Avalonia measures dashes in multiples of the stroke thickness; the design
        // gives them in user units, the way SVG does. Dividing here keeps the
        // numbers below identical to the ones in the design file.
        var dashes = new DashStyle([22 / thickness, 8 / thickness], 0);

        var pen = new Pen(brush, thickness, dashes, lineCap: PenLineCap.Round);

        using var turning = context.PushTransform(
            Matrix.CreateTranslation(-centre.X, -centre.Y) *
            Matrix.CreateRotation(phase * 1.6) *
            Matrix.CreateTranslation(centre.X, centre.Y));

        context.DrawEllipse(null, pen, centre, DotRadius, DotRadius);
    }
}

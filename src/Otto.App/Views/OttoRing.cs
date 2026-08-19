using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Otto.Core;

namespace Otto.App.Views;

/// <summary>
/// What the ring looks like in one state, before anything moves.
///
/// A record rather than four switch expressions scattered through
/// <see cref="OttoRing.Render"/>, so the whole of a state's appearance can be read
/// — and pinned by a test — in one place. The design file is not in this
/// repository; these numbers are the only copy of it that ships.
/// </summary>
/// <param name="Fill">The disc behind everything. Always the same near-black.</param>
/// <param name="FillOpacity">How much of the desktop shows through the disc.</param>
/// <param name="Stroke">The ring itself, which is what carries the state.</param>
/// <param name="StrokeOpacity">Solid for the coloured states, translucent for the quiet ones.</param>
/// <param name="StrokeWidth">Thickest while listening: the state that has to read from furthest away.</param>
public readonly record struct RingSpec(
    Color Fill,
    double FillOpacity,
    Color Stroke,
    double StrokeOpacity,
    double StrokeWidth);

/// <summary>
/// The middle overlay: a ring with audio bars and no face.
///
/// <para>
/// Section F17 of the redesign, and the middle of three appearances. The character
/// is a drawing with poses and a personality; <see cref="OttoGlyph"/> is a dot and
/// three lines that only change colour; this sits between them, and takes less
/// screen than either the character or its own design canvas would suggest. It
/// reads its state from shape and colour alone, which is why the same motif turns
/// up in the tray icon and the window header.
/// </para>
/// <para>
/// Everything is drawn on the design's own 144×144 canvas and scaled to the
/// control's bounds, so the numbers below can be checked against the design file
/// line by line without arithmetic in between — and so the overlay's size on
/// screen is one constant to change rather than forty.
/// </para>
/// </summary>
public sealed class OttoRing : Control
{
    public static readonly StyledProperty<DictationState> StateProperty =
        AvaloniaProperty.Register<OttoRing, DictationState>(nameof(State));

    public DictationState State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>
    /// The canvas the design was drawn on. Every coordinate in this file is in
    /// these units, so they can be checked against the design file without
    /// arithmetic in between.
    ///
    /// Not the size on screen — see <see cref="WindowSize"/>. Keeping the two apart
    /// is what lets the overlay be resized without a single design number moving.
    /// </summary>
    public const double DesignSize = 144;

    /// <summary>
    /// How much screen the ring actually takes: 40% less than the canvas it is
    /// drawn on.
    ///
    /// The character needs its 144 px because the poses stop being distinguishable
    /// below it. This has four states told by colour and one shape, and it read as
    /// oversized at the character's size — it was borrowing a footprint it does not
    /// use. Shrinking is free here precisely because nothing in the drawing is
    /// measured in pixels.
    /// </summary>
    public const double WindowSize = DesignSize * 0.6;

    private const double Centre = DesignSize / 2;
    private const double Radius = 46;

    /// <summary>The halo that only listening draws, outside the ring proper.</summary>
    private const double HaloRadius = 58;

    private static readonly Color Ink = Color.FromRgb(0x0C, 0x0E, 0x12);
    private static readonly Color Red = Color.FromRgb(0xFF, 0x24, 0x38);
    private static readonly Color Amber = Color.FromRgb(0xD9, 0x7A, 0x08);
    private static readonly Color Halo = Color.FromRgb(0xE5, 0x48, 0x4A);

    /// <summary>
    /// The ring's appearance per state, straight from the design.
    ///
    /// Public for the same reason <see cref="OttoGlyph.Palette"/> is: these values
    /// have no other source in the repository, so only a test stops them drifting.
    /// </summary>
    public static RingSpec Spec(DictationState state) => state switch
    {
        DictationState.Recording    => new(Ink, 0.38, Red,          1.00, 6.0),
        DictationState.Transcribing => new(Ink, 0.34, Amber,        1.00, 5.5),
        DictationState.Loading      => new(Ink, 0.30, Colors.White, 0.22, 5.0),
        _                           => new(Ink, 0.30, Colors.White, 0.55, 5.0),
    };

    /// <summary>
    /// The three bars, as (centre, width, height) on the design canvas. All three
    /// are centred vertically on the ring's own centre, so a bar grows in both
    /// directions and the group never looks like it is sitting on a shelf.
    ///
    /// Listening is the only state that changes them, and it changes all three:
    /// taller, slightly wider, and spread a touch further apart.
    ///
    /// <para>
    /// The design calls these "barras siguen tu voz". They do not yet — nothing in
    /// <see cref="IAudioCapture"/> reports a level, so there is no voice to follow.
    /// What ships is the design's shape with a gentle motion over it, which reads
    /// as "listening" without claiming to be a meter. Making it real means adding a
    /// level to that port and is a change to the audio boundary, not to this file.
    /// </para>
    /// </summary>
    public static (double Centre, double Width, double Height)[] Bars(DictationState state) =>
        state == DictationState.Recording
            ? [(61.5, 7, 22), (72, 7, 38), (82.5, 7, 14)]
            : [(62, 6, 10), (72, 6, 16), (82, 6, 8)];

    /// <summary>
    /// The three marching dots that replace the bars while transcribing. Fixed
    /// opacities in the design; here they are rotated between the three so the
    /// group reads as moving rather than as one dot being brighter for no reason.
    /// </summary>
    private static readonly double[] DotOpacities = [0.35, 0.7, 1.0];

    private const double DotRadius = 5;

    private readonly DispatcherTimer timer;
    private double phase;

    public OttoRing()
    {
        // ~30 fps, the same budget OttoCharacter spends. This one is on screen at
        // the same size and something moves in every state, so the cheaper 20 fps
        // the minimal glyph gets away with would show on the rotating arc.
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, Tick);
        timer.Start();
    }

    static OttoRing() => AffectsRender<OttoRing>(StateProperty);

    private void Tick(object? sender, EventArgs e)
    {
        phase += 0.033;
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        var scale = Math.Min(Bounds.Width, Bounds.Height) / DesignSize;
        if (scale <= 0) return;

        var transform =
            Matrix.CreateScale(scale, scale) *
            Matrix.CreateTranslation(
                (Bounds.Width - DesignSize * scale) / 2,
                (Bounds.Height - DesignSize * scale) / 2);

        using var scaled = context.PushTransform(transform);

        var spec = Spec(State);
        var middle = new Point(Centre, Centre);

        // Idle breathes: the whole ring swells by a couple of percent over about
        // four seconds. It is the slowest thing on screen on purpose — enough that
        // the overlay is not mistaken for a frozen window, little enough that it
        // never pulls the eye away from what the user is actually doing.
        var breath = State == DictationState.Idle ? 1 + Math.Sin(phase * 1.6) * 0.02 : 1;

        using var breathing = context.PushTransform(
            Matrix.CreateTranslation(-Centre, -Centre) *
            Matrix.CreateScale(breath, breath) *
            Matrix.CreateTranslation(Centre, Centre));

        if (State == DictationState.Recording) DrawHalo(context, middle);

        context.DrawEllipse(new SolidColorBrush(spec.Fill, spec.FillOpacity), null, middle, Radius, Radius);

        DrawRing(context, middle, spec);

        if (State == DictationState.Transcribing) DrawDots(context);
        else DrawBars(context, spec);
    }

    /// <summary>
    /// The outer ring listening draws, pulsing outward. Nothing else has one, which
    /// is what makes "Otto is hearing you right now" legible across a room rather
    /// than only up close.
    /// </summary>
    private void DrawHalo(DrawingContext context, Point middle)
    {
        var pulse = (Math.Sin(phase * 5) + 1) / 2;
        var radius = HaloRadius + pulse * 3;

        context.DrawEllipse(null, new Pen(new SolidColorBrush(Halo, 0.35), 3), middle, radius, radius);
    }

    /// <summary>
    /// Solid in every state but loading, where it is the dim base with a bright arc
    /// turning on top of it — the design's "arco gira".
    /// </summary>
    private void DrawRing(DrawingContext context, Point middle, RingSpec spec)
    {
        var pen = new Pen(new SolidColorBrush(spec.Stroke, spec.StrokeOpacity), spec.StrokeWidth);

        context.DrawEllipse(null, pen, middle, Radius, Radius);

        if (State != DictationState.Loading) return;

        // Avalonia measures dashes in multiples of the stroke thickness; the design
        // gives them in user units, as SVG does. Dividing here keeps 200 and 90
        // readable as the same numbers that are in the design file.
        var dashes = new DashStyle([200 / spec.StrokeWidth, 90 / spec.StrokeWidth], 0);

        var arc = new Pen(new SolidColorBrush(spec.Stroke, 0.75), spec.StrokeWidth, dashes);

        using var turning = context.PushTransform(
            Matrix.CreateTranslation(-Centre, -Centre) *
            Matrix.CreateRotation(phase * 2.2) *
            Matrix.CreateTranslation(Centre, Centre));

        context.DrawEllipse(null, arc, middle, Radius, Radius);
    }

    /// <summary>
    /// Three bars, centred on the ring's centre. White while listening — against
    /// the red ring that is the highest contrast pair available — and the ring's
    /// own colour, dimmed, in the states where they are furniture rather than
    /// signal.
    /// </summary>
    private void DrawBars(DrawingContext context, RingSpec spec)
    {
        var listening = State == DictationState.Recording;

        var brush = listening
            ? new SolidColorBrush(Colors.White)
            : new SolidColorBrush(spec.Stroke, State == DictationState.Loading ? 0.25 : spec.StrokeOpacity);

        var bars = Bars(State);

        for (var i = 0; i < bars.Length; i++)
        {
            var (centre, width, height) = bars[i];

            // See Bars: a stand-in for a level this pipeline does not report yet.
            // Each bar runs at its own rate so the three never move as one block,
            // which is the tell that would give away a loop.
            if (listening) height *= 1 + Math.Sin(phase * (7 + i * 2.5) + i) * 0.28;

            var rect = new Rect(centre - width / 2, Centre - height / 2, width, height);

            context.DrawRectangle(brush, null, rect);
        }
    }

    /// <summary>
    /// Transcribing swaps the bars for three dots whose brightness rotates between
    /// them. Bars that stopped moving would read as a stalled meter; dots taking
    /// turns read as work being done.
    /// </summary>
    private void DrawDots(DrawingContext context)
    {
        // One step every ~280 ms. Slow enough to be followed, fast enough that the
        // typical two-second transcription shows several.
        var step = (int)(phase * 3.6);

        for (var i = 0; i < 3; i++)
        {
            var opacity = DotOpacities[(i + step) % DotOpacities.Length];

            var centre = new Point(58 + i * 14, Centre);

            context.DrawEllipse(new SolidColorBrush(Colors.White, opacity), null, centre, DotRadius, DotRadius);
        }
    }
}

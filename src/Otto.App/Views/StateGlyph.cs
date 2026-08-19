using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Otto.Core;

namespace Otto.App.Views;

/// <summary>
/// The state motif at header size: the same four figures the tray icon uses,
/// drawn as vectors instead of rasterised.
///
/// <para>
/// It shares <see cref="StateShapes"/> with <see cref="TrayIcons"/> rather than
/// re-describing the shapes, which is the whole reason the header and the
/// notification area read as the same thing. Static by design — the header already
/// says what is happening in words beside it, and a second moving element next to
/// a line of text is noise.
/// </para>
/// </summary>
public sealed class StateGlyph : Control
{
    public static readonly StyledProperty<DictationState> StateProperty =
        AvaloniaProperty.Register<StateGlyph, DictationState>(nameof(State));

    public DictationState State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    static StateGlyph() => AffectsRender<StateGlyph>(StateProperty);

    public override void Render(DrawingContext context)
    {
        var scale = Math.Min(Bounds.Width, Bounds.Height) / StateShapes.Canvas;
        if (scale <= 0) return;

        using var scaled = context.PushTransform(
            Matrix.CreateScale(scale, scale) *
            Matrix.CreateTranslation(
                (Bounds.Width - StateShapes.Canvas * scale) / 2,
                (Bounds.Height - StateShapes.Canvas * scale) / 2));

        var shape = StateShapes.For(State);
        var brush = new SolidColorBrush(shape.Colour);
        var middle = new Point(StateShapes.Centre, StateShapes.Centre);

        switch (shape.Kind)
        {
            case StateShapeKind.Bars:
                foreach (var (x, y, w, h) in StateShapes.Bars)
                    context.DrawRectangle(brush, null, new Rect(x, y, w, h));
                break;

            case StateShapeKind.Disc:
                context.DrawEllipse(brush, null, middle, shape.Radius, shape.Radius);
                break;

            case StateShapeKind.DashedRing:
                // Avalonia measures dashes in multiples of the stroke thickness; the
                // design gives them in canvas units along the circumference, as SVG
                // does. Dividing here keeps the shared numbers unconverted.
                context.DrawEllipse(
                    null,
                    new Pen(brush, shape.Thickness,
                        new DashStyle([shape.DashOn / shape.Thickness, shape.DashOff / shape.Thickness], 0)),
                    middle, shape.Radius, shape.Radius);
                break;

            default:
                context.DrawEllipse(null, new Pen(brush, shape.Thickness), middle, shape.Radius, shape.Radius);
                break;
        }
    }
}

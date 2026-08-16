using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Otto.Core;

namespace Otto.App.Views;

/// <summary>
/// The little face that floats on screen while Otto is running.
///
/// Drawn rather than loaded from a Lottie file, for the same reason as the tray
/// icons: the state is the single source of truth for what appears, there are no
/// binary assets to keep in sync, and the whole thing stays readable as code.
///
/// It answers two questions without the user going to look for anything — is Otto
/// on, and what is it doing right now.
/// </summary>
public sealed class OttoCharacter : Control
{
    public static readonly StyledProperty<DictationState> StateProperty =
        AvaloniaProperty.Register<OttoCharacter, DictationState>(nameof(State));

    public DictationState State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    private readonly DispatcherTimer timer;
    private double phase;
    private double blink = 1;
    private double nextBlink = 3;

    public OttoCharacter()
    {
        // ~30 fps. Enough for a bob and a blink, cheap enough that a decoration
        // never shows up in the user's battery or fan noise.
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, Tick);
        timer.Start();
    }

    static OttoCharacter() => AffectsRender<OttoCharacter>(StateProperty);

    private void Tick(object? sender, EventArgs e)
    {
        phase += 0.033;

        if (State is DictationState.Recording or DictationState.Transcribing)
        {
            // Eyes stay open while it is working: a blink would read as inattention
            // at exactly the moment the user wants to know it is listening.
            blink = 1;
        }
        else if (phase > nextBlink)
        {
            var since = phase - nextBlink;

            // Down and back up over ~180 ms.
            blink = since < 0.18 ? Math.Abs(since - 0.09) / 0.09 : 1;

            if (since >= 0.18) nextBlink = phase + 2.5 + Random.Shared.NextDouble() * 3;
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;

        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = size * 0.30;

        // A slow bob, so it reads as alive rather than as a stuck sticker.
        var bob = State == DictationState.Idle ? Math.Sin(phase * 1.6) * size * 0.015 : 0;
        centre = new Point(centre.X, centre.Y + bob);

        var (body, accent) = Palette(State);

        DrawAura(context, centre, radius, size, accent);

        context.DrawEllipse(new SolidColorBrush(body), null, centre, radius, radius);
        context.DrawEllipse(null, new Pen(new SolidColorBrush(accent, 0.9), size * 0.022), centre, radius, radius);

        DrawEyes(context, centre, radius);
    }

    /// <summary>The halo carries the state: a pulse while listening, a sweep while thinking.</summary>
    private void DrawAura(DrawingContext context, Point centre, double radius, double size, Color accent)
    {
        switch (State)
        {
            case DictationState.Recording:
            {
                var pulse = (Math.Sin(phase * 5) + 1) / 2;
                var outer = radius + size * (0.06 + pulse * 0.08);

                context.DrawEllipse(new SolidColorBrush(accent, 0.16 + pulse * 0.12), null, centre, outer, outer);
                break;
            }

            case DictationState.Transcribing:
            {
                for (var i = 0; i < 3; i++)
                {
                    var angle = phase * 3 + i * (Math.PI * 2 / 3);
                    var distance = radius + size * 0.11;

                    var dot = new Point(
                        centre.X + Math.Cos(angle) * distance,
                        centre.Y + Math.Sin(angle) * distance);

                    context.DrawEllipse(new SolidColorBrush(accent, 0.85), null, dot, size * 0.035, size * 0.035);
                }

                break;
            }

            case DictationState.Loading:
            {
                context.DrawEllipse(new SolidColorBrush(accent, 0.10), null, centre, radius + size * 0.05, radius + size * 0.05);
                break;
            }
        }
    }

    private void DrawEyes(DrawingContext context, Point centre, double radius)
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x14, 0x1B, 0x2B));

        var offsetX = radius * 0.40;
        var offsetY = radius * 0.08;
        var eyeRadius = radius * 0.17;

        // Wider while listening — the cheapest way to show attention.
        if (State == DictationState.Recording) eyeRadius *= 1.25;

        foreach (var side in new[] { -1, 1 })
        {
            var eye = new Point(centre.X + side * offsetX, centre.Y - offsetY);

            // Squashing vertically is the blink; a circle scaled to a slit reads
            // as an eyelid without needing one.
            context.DrawEllipse(brush, null, eye, eyeRadius, Math.Max(eyeRadius * blink, radius * 0.02));
        }
    }

    private static (Color Body, Color Accent) Palette(DictationState state) => state switch
    {
        DictationState.Loading      => (Color.FromRgb(0xD9, 0xDD, 0xE5), Color.FromRgb(0x8A, 0x8A, 0x8A)),
        DictationState.Recording    => (Color.FromRgb(0xFF, 0xE3, 0xE1), Color.FromRgb(0xE5, 0x48, 0x4A)),
        DictationState.Transcribing => (Color.FromRgb(0xFF, 0xF0, 0xD6), Color.FromRgb(0xE8, 0xA3, 0x3D)),
        _                           => (Color.FromRgb(0xE4, 0xF3, 0xE9), Color.FromRgb(0x4C, 0xAF, 0x76)),
    };

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Otto.Core;

namespace Otto.App.Views;

/// <summary>
/// What Otto is doing, as far as the user can see.
///
/// Deliberately a larger set than <see cref="DictationState"/>. The pipeline only
/// needs four states to work; a character needs more than four things to say, and
/// none of the extra ones — being startled, being pleased, having given up and
/// poured a glass of wine — is information the pipeline should have to carry.
/// </summary>
public enum OttoPose
{
    Neutral,
    Waiting,
    Listening,
    Thinking,
    Lounging,
    Startled,
    Pleased,
    Annoyed,
}

/// <summary>
/// The little demon that floats on screen while Otto is running.
///
/// It answers two questions without the user going to look for anything — is Otto
/// on, and what is it doing right now.
///
/// <para>
/// Each pose is one drawing, swapped with a cross-fade and moved with transforms.
/// The motion is code rather than video or a Lottie file because this window is
/// click-through and transparent, so everything drawn needs a real alpha channel;
/// because a loop has to close without a seam; and because adding a state should
/// cost a case, not a new asset pipeline.
/// </para>
/// <para>
/// If the artwork is missing the original hand-drawn face takes over, so an absent
/// asset costs personality and never costs a working overlay — the same rule the
/// post processor follows.
/// </para>
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

    /// <summary>How long Otto waits before deciding nobody is coming back.</summary>
    private static readonly TimeSpan LoungeAfter = TimeSpan.FromMinutes(10);

    private const double FadeSeconds = 0.16;

    /// <summary>
    /// Where the character's feet sit in each drawing, as a fraction of the canvas
    /// down from the top.
    ///
    /// Measured from the artwork rather than guessed: the poses were drawn
    /// separately and their baselines differ by up to 4% of the canvas, which is
    /// enough that swapping to <see cref="OttoPose.Listening"/> made him visibly
    /// hop. Everything is drawn aligned to <see cref="Baseline"/> instead of to
    /// the canvas, so a pose can be redrawn without the others moving.
    ///
    /// To re-measure after changing the art: find the lowest row of each PNG with
    /// an alpha above ~24 and divide by the canvas height.
    /// </summary>
    private const double Baseline = 0.953;

    private static readonly Dictionary<OttoPose, (string File, double Feet)> Art = new()
    {
        [OttoPose.Neutral]   = ("neutral.png",      0.953),
        [OttoPose.Waiting]   = ("daigual.png",      0.961),
        [OttoPose.Listening] = ("escuchando.png",   0.916),
        [OttoPose.Thinking]  = ("pensando.png",     0.959),
        [OttoPose.Lounging]  = ("descansando.png",  0.967),
        [OttoPose.Startled]  = ("sorprendido.png",  0.947),
        [OttoPose.Pleased]   = ("entusiasmado.png", 0.962),
        [OttoPose.Annoyed]   = ("enojado.png",      0.952),
    };

    /// <summary>Decoded on first use, then kept. Null means that pose has no artwork.</summary>
    private static readonly Dictionary<OttoPose, Bitmap?> Decoded = [];

    private readonly DispatcherTimer timer;
    private double phase;
    private double blink = 1;
    private double nextBlink = 3;

    private DictationState shown = DictationState.Loading;
    private double reactedAt = -10;

    private double fidgetAt = -10;
    private double nextFidget = 5;

    private OttoPose pose = OttoPose.Waiting;
    private OttoPose fading = OttoPose.Waiting;
    private double fadeStartedAt = -10;

    /// <summary>A pose that overrides the state for a moment, then expires.</summary>
    private OttoPose? interjection;
    private double interjectionUntil;

    /// <summary>When Otto last had something to do, for the lounging timer.</summary>
    private double busyAt;

    public OttoCharacter()
    {
        // ~30 fps. Enough for a bob and a blink, cheap enough that a decoration
        // never shows up in the user's battery or fan noise.
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, Tick);
        timer.Start();
    }

    static OttoCharacter() => AffectsRender<OttoCharacter>(StateProperty);

    /// <summary>
    /// Shows a pose for a moment and then lets the state take over again.
    ///
    /// This is how the reactions work: a dictation landing or a silence being
    /// heard is an event, not a state, and holding either one on screen would be
    /// lying about what Otto is doing now.
    /// </summary>
    public void React(OttoPose momentary, double seconds = 1.4)
    {
        interjection = momentary;
        interjectionUntil = phase + seconds;
        busyAt = phase;
    }

    private static Bitmap? Load(OttoPose which)
    {
        if (Decoded.TryGetValue(which, out var cached)) return cached;

        var uri = new Uri($"avares://Otto.App/Assets/poses/{Art[which].File}");

        try
        {
            return Decoded[which] = AssetLoader.Exists(uri) ? new Bitmap(AssetLoader.Open(uri)) : null;
        }
        catch
        {
            // A decoration that cannot be loaded is not a reason to fail.
            return Decoded[which] = null;
        }
    }

    /// <summary>
    /// The pose the current situation calls for, before any cross-fade.
    /// </summary>
    private OttoPose Wanted()
    {
        if (interjection is { } momentary && phase < interjectionUntil) return momentary;

        return State switch
        {
            DictationState.Loading      => OttoPose.Waiting,
            DictationState.Recording    => OttoPose.Listening,
            DictationState.Transcribing => OttoPose.Thinking,

            // Nobody has dictated in a long while. He is not needed and stops
            // pretending otherwise.
            _ when phase - busyAt > LoungeAfter.TotalSeconds => OttoPose.Lounging,

            _ => OttoPose.Neutral,
        };
    }

    private void Tick(object? sender, EventArgs e)
    {
        phase += 0.033;

        // Switching behaviour between one frame and the next reads as a cut, not
        // as a character noticing something. The change is stamped here so the
        // drawing can spring into the new state instead of appearing in it.
        if (State != shown)
        {
            // Caught off guard for a moment before settling into listening. The
            // startle is what sells the hotkey as having done something.
            if (State == DictationState.Recording && shown != DictationState.Recording)
                React(OttoPose.Startled, 0.45);

            shown = State;
            reactedAt = phase;
        }

        if (State != DictationState.Idle) busyAt = phase;

        var wanted = Wanted();

        if (wanted != pose)
        {
            fading = pose;
            pose = wanted;
            fadeStartedAt = phase;
        }

        // A loop built from one sine wave is a metronome, and the eye picks that
        // up within seconds. Every few seconds Otto does something small and
        // off-beat, which is what stops the idle from reading as a screensaver.
        if (pose == OttoPose.Neutral && phase > nextFidget)
        {
            fidgetAt = phase;
            nextFidget = phase + 6 + Random.Shared.NextDouble() * 6;
        }
        else if (pose != OttoPose.Neutral)
        {
            nextFidget = phase + 3;
        }

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

        var (body, accent) = Palette(State);

        DrawAura(context, centre, radius, size, accent);

        if (Load(pose) is null)
        {
            DrawFace(context, centre, radius, size, body, accent);
            return;
        }

        var motion = Motion(size);

        // The outgoing pose keeps moving while it fades, so the cross-fade reads
        // as one character changing its mind rather than two drawings dissolving.
        var mix = Math.Clamp((phase - fadeStartedAt) / FadeSeconds, 0, 1);

        if (mix < 1 && Load(fading) is { } leaving)
        {
            using var faded = context.PushOpacity(1 - mix);
            DrawPose(context, leaving, Art[fading].Feet, centre, size, motion);
        }

        using var entering = context.PushOpacity(mix);
        DrawPose(context, Load(pose)!, Art[pose].Feet, centre, size, motion);
    }

    /// <summary>
    /// A spring that settles: a big kick that is gone in about a second.
    ///
    /// This is what makes a state change look like Otto noticed something rather
    /// than like the animation was swapped out. Without it the character is
    /// already in its new behaviour on the first frame, which is exactly how a
    /// slideshow behaves.
    /// </summary>
    private double Spring(double amplitude, double frequency = 13, double decay = 6)
    {
        var since = phase - reactedAt;

        return since > 2 ? 0 : amplitude * Math.Exp(-since * decay) * Math.Sin(since * frequency);
    }

    private readonly record struct Movement(double Bob, double Drift, double Tilt, double ScaleX, double ScaleY);

    /// <summary>
    /// Where the character is this frame, before anything is drawn.
    ///
    /// Computed once and shared by both sides of a cross-fade, so the two drawings
    /// occupy the same place and the swap cannot look like a jump.
    ///
    /// Three rules do most of the work:
    /// <list type="bullet">
    /// <item>Everything pivots at the feet, never at the centre. A drawing rotated
    /// about its middle spins like a sticker; rotated about the ground it leans,
    /// and weight is most of what makes a character read as physical.</item>
    /// <item>The squash keeps its area — it widens as it flattens — because
    /// scaling one axis alone looks like a stretched image, not a breath.</item>
    /// <item>Bob and sway run at different rates, so the path traced is a lazy
    /// figure of eight instead of a straight line up and down.</item>
    /// </list>
    /// </summary>
    private Movement Motion(double size)
    {
        double bob = 0, drift = 0, tilt = 0, breath, lift = 1;

        switch (pose)
        {
            case OttoPose.Neutral:
            case OttoPose.Pleased:
            {
                bob = Math.Sin(phase * 1.5) * size * 0.045;

                // Deliberately not a multiple of the bob rate: matching rates
                // would collapse the figure of eight back into a line.
                drift = Math.Sin(phase * 1.1 + 0.9) * size * 0.016;
                tilt = Math.Sin(phase * 0.9 + 0.4) * 0.045;

                breath = Math.Sin(phase * 1.5) * 0.035;

                // The fidget: a quick irritated shiver, over in under a second.
                var since = phase - fidgetAt;

                if (since < 0.9)
                {
                    var fade = Math.Exp(-since * 4);
                    tilt += Math.Sin(since * 26) * 0.075 * fade;
                    bob -= size * 0.02 * fade;
                }

                break;
            }

            case OttoPose.Listening:
            case OttoPose.Startled:
            {
                // Up on its toes and holding still. Stillness is what reads as
                // attention — a character that keeps bobbing while you talk looks
                // like it is not listening.
                bob = -size * 0.045;
                lift = 1.06;

                // A fine tremor, near the limit of what you notice. It is the
                // difference between "paused" and "straining to catch every word".
                drift = Math.Sin(phase * 23) * size * 0.005;
                breath = Math.Sin(phase * 7) * 0.014;
                break;
            }

            case OttoPose.Thinking:
            {
                // Rocking side to side, the way anyone works something out.
                tilt = Math.Sin(phase * 1.9) * 0.13;
                drift = Math.Sin(phase * 1.9) * size * 0.028;

                // Double time against the rocking, so it reads as nodding along
                // rather than as one single swing.
                bob = Math.Sin(phase * 3.8) * size * 0.018;
                breath = Math.Sin(phase * 3.0) * 0.022;
                break;
            }

            case OttoPose.Lounging:
            {
                // Barely moving. He is sitting in a beanbag with a drink; anything
                // more energetic would undercut the whole joke.
                breath = Math.Sin(phase * 0.7) * 0.014;
                tilt = Math.Sin(phase * 0.35) * 0.012;
                break;
            }

            case OttoPose.Annoyed:
            {
                // A short, stiff refusal to move.
                breath = Math.Sin(phase * 2.2) * 0.010;
                tilt = -0.03;
                break;
            }

            default:
            {
                // Slumped and dim: it genuinely cannot do anything for you yet
                // and should not look like it can.
                bob = size * 0.022;
                tilt = 0.05;
                breath = Math.Sin(phase * 0.9) * 0.012;
                lift = 0.97;
                break;
            }
        }

        // The reaction to whatever just changed, on top of the resting behaviour.
        tilt += Spring(0.16);
        bob += Spring(size * 0.05, frequency: 11, decay: 7);

        var squash = 1 + breath + Spring(0.06, frequency: 15, decay: 8);

        return new Movement(bob, drift, tilt, (2 - squash) * lift, squash * lift);
    }

    /// <summary>
    /// Draws one pose, aligned so its feet land on the shared baseline rather than
    /// wherever that particular drawing happened to sit on its canvas.
    /// </summary>
    private static void DrawPose(
        DrawingContext context, Bitmap art, double feet, Point centre, double size, Movement move)
    {
        var side = size * 0.92;

        // The whole canvas maps onto `side`, so a fraction of the canvas is the
        // same fraction of the drawn square.
        var align = (Baseline - feet) * side;

        var target = new Rect(centre.X - side / 2, centre.Y - side / 2 + align, side, side);

        // Its feet, roughly. Everything turns and squashes around this point.
        var pivot = new Point(centre.X, centre.Y + side * 0.44);

        var transform =
            Matrix.CreateTranslation(-pivot.X, -pivot.Y) *
            Matrix.CreateScale(move.ScaleX, move.ScaleY) *
            Matrix.CreateRotation(move.Tilt) *
            Matrix.CreateTranslation(pivot.X + move.Drift, pivot.Y + move.Bob);

        using var moved = context.PushTransform(transform);

        context.DrawImage(art, target);
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

    /// <summary>
    /// The fallback face, for when no artwork has been added.
    ///
    /// It blinks and the artwork does not, and that is on purpose rather than an
    /// oversight: a blink painted on top of a finished drawing has to know exactly
    /// where the eyes are, and guessing wrong looks worse than not blinking at
    /// all. The poses say what the blink used to say.
    /// </summary>
    private void DrawFace(DrawingContext context, Point centre, double radius, double size, Color body, Color accent)
    {
        var bob = State == DictationState.Idle ? Math.Sin(phase * 1.6) * size * 0.015 : 0;
        centre = new Point(centre.X, centre.Y + bob);

        context.DrawEllipse(new SolidColorBrush(body), null, centre, radius, radius);
        context.DrawEllipse(null, new Pen(new SolidColorBrush(accent, 0.9), size * 0.022), centre, radius, radius);

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

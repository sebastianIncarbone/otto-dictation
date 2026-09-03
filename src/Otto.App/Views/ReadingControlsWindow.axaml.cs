using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Otto.Core;

namespace Otto.App.Views;

/// <summary>
/// The transport for a reading in progress: repeat, pause, speed.
///
/// <para>
/// A window of its own rather than a panel inside <see cref="CharacterWindow"/>, and the
/// reason is one extended style. The character is scenery — it carries
/// <c>WS_EX_TRANSPARENT</c> and <c>IsHitTestVisible="False"</c> so clicks pass straight
/// through to the document underneath, which is exactly what a pause button must not do.
/// What both windows keep is <c>WS_EX_NOACTIVATE</c>, and it matters more here: a card
/// that took focus when the user pressed pause would leave Otto as the foreground window,
/// and the next dictation would paste into Otto instead of into what they were reading.
/// See <see cref="IOverlayStyler.MakeNonActivating"/>.
/// </para>
/// <para>
/// Shown only while a reading is happening. A transport for audio that is not playing is
/// three dead buttons floating over somebody's screen, and this feature's whole posture —
/// optional, quiet, out of the way — is against that.
/// </para>
/// </summary>
public partial class ReadingControlsWindow : Window
{
    /// <summary>How far above the character the card sits, when there is a character.</summary>
    private const int Gap = 10;

    private readonly IOverlayStyler? styler;
    private readonly ReadingPipeline? reading;

    /// <summary>
    /// Where to sit when the overlay is switched off, matching what
    /// <c>CharacterWindow.MoveToCorner</c> uses so the two never disagree about the corner.
    /// </summary>
    private const int CornerMargin = 24;

    public ReadingControlsWindow() => InitializeComponent();

    public ReadingControlsWindow(IOverlayStyler styler, ReadingPipeline reading) : this()
    {
        this.styler = styler;
        this.reading = reading;

        RepeatButton.Click += (_, _) => reading.Repeat();
        PauseButton.Click += (_, _) => reading.TogglePause();

        // Cycles rather than opening a menu: a menu is a second click and a target to aim
        // at, and this is pressed by somebody whose attention is on what is being read.
        SpeedButton.Click += (_, _) => reading.Speed = reading.Speed.Next();

        reading.StateChanged += state => Dispatcher.UIThread.Post(() => Apply(state));
        reading.SpeedChanged += speed => Dispatcher.UIThread.Post(() => Apply(speed));

        Apply(reading.Speed);
    }

    /// <summary>
    /// The window this one floats above, or null when the user has the overlay switched
    /// off. Held rather than looked up so a card shown before the character exists still
    /// finds its corner.
    /// </summary>
    public Window? Anchor { get; set; }

    /// <summary>
    /// The pause button is the only control whose label is a state rather than an action,
    /// because it is the only one with two.
    /// </summary>
    private void Apply(ReadingState state)
    {
        var paused = state == ReadingState.Paused;

        PauseButton.Content = paused ? "▶" : "⏸";
        ToolTip.SetTip(PauseButton, paused ? "Seguir" : "Pausar");
    }

    /// <summary>The speed button's label IS the current speed — see the XAML comment.</summary>
    private void Apply(ReadingSpeed speed) => SpeedButton.Content = speed.Label;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // The handle only exists once the window is open, so the styles cannot be applied
        // any earlier — same constraint CharacterWindow.OnOpened works around.
        if (TryGetPlatformHandle() is { } handle)
            styler?.MakeNonActivating(handle.Handle);

        if (reading is not null) Apply(reading.State);

        MoveIntoPlace();
    }

    /// <summary>
    /// Above the character when there is one, in the same corner when there is not.
    ///
    /// <para>
    /// Above rather than beside: the character sits at the bottom-right against the
    /// taskbar, so there is no room below it and putting the card to its left would have
    /// it drift across the screen every time the appearance changes size.
    /// </para>
    /// <para>
    /// Clamped to the working area, because <c>SizeToContent</c> means this window's own
    /// height is not known until it is open — and a card pushed off the top of a small
    /// screen by a tall character would be a transport nobody can reach.
    /// </para>
    /// </summary>
    public void MoveIntoPlace()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;

        var area = screen.WorkingArea;
        var width = (int)(Width * screen.Scaling);
        var height = (int)(Height * screen.Scaling);

        var x = area.X + area.Width - width - CornerMargin;
        var y = Anchor is { IsVisible: true } anchor
            ? anchor.Position.Y - height - Gap
            : area.Y + area.Height - height - CornerMargin;

        Position = new PixelPoint(x, Math.Max(area.Y + CornerMargin, y));
    }
}

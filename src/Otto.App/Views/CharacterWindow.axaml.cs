using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Otto.Core;

namespace Otto.App.Views;

/// <summary>
/// The window the character lives in: borderless, transparent, always on top, and
/// transparent to input.
///
/// Avalonia gets it most of the way there. The rest — clicks passing through and,
/// critically, never taking focus — needs platform styles applied once the native
/// handle exists, which is what <see cref="IOverlayStyler"/> is for.
/// </summary>
public partial class CharacterWindow : Window
{
    private readonly IOverlayStyler? styler;

    // InitializeComponent, not AvaloniaXamlLoader.Load: only the generated version
    // wires up the x:Name fields, and this window needs a reference to Character.
    public CharacterWindow() => InitializeComponent();

    public CharacterWindow(IOverlayStyler styler) : this() => this.styler = styler;

    public void Follow(DictationPipeline pipeline)
    {
        Character.State = pipeline.State;

        pipeline.StateChanged += state =>
            Dispatcher.UIThread.Post(() => Character.State = state);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // The handle only exists once the window is open, so the styles cannot be
        // applied any earlier.
        if (TryGetPlatformHandle() is { } handle)
            styler?.MakeClickThrough(handle.Handle);

        MoveToCorner();
    }

    /// <summary>
    /// Bottom-right, clear of the taskbar. Out of the way of what people read and
    /// type, and next to where the tray icon already is.
    /// </summary>
    private void MoveToCorner()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;

        var area = screen.WorkingArea;
        var margin = 24;

        Position = new PixelPoint(
            area.X + area.Width - (int)(Width * screen.Scaling) - margin,
            area.Y + area.Height - (int)(Height * screen.Scaling) - margin);
    }
}

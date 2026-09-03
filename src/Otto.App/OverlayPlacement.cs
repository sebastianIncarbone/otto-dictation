using Avalonia;

namespace Otto.App;

/// <summary>
/// Where the character overlay should sit, decided without touching a window.
///
/// <para>
/// Split out of <c>CharacterWindow</c> as a pure function over rectangles because the
/// interesting case is the one that is hardest to reproduce by hand: a position saved on a
/// second monitor that is no longer connected. Otto opens at coordinates that do not exist
/// any more, on no screen, and the user has an invisible character and a setting they
/// cannot reach to fix it. That deserves a test, and a test cannot have a monitor
/// unplugged for it.
/// </para>
/// </summary>
public static class OverlayPlacement
{
    /// <summary>
    /// The gap from the screen edge. Shared with the reading controls so the two agree
    /// about where the corner is.
    /// </summary>
    public const int Margin = 24;

    /// <summary>
    /// How much of the overlay has to be on a screen for a stored position to be honoured.
    ///
    /// <para>
    /// Not "any overlap at all". A window one pixel onto the desktop is technically visible
    /// and practically lost — there is nothing there to grab and drag back. This is roughly
    /// a finger's worth of target.
    /// </para>
    /// </summary>
    public const int MinimumVisible = 24;

    /// <summary>
    /// Bottom-right, clear of the taskbar. Out of the way of what people read and type, and
    /// next to where the tray icon already is.
    /// </summary>
    public static PixelPoint Corner(PixelRect area, PixelSize size) =>
        new(area.X + area.Width - size.Width - Margin,
            area.Y + area.Height - size.Height - Margin);

    /// <summary>
    /// The stored position when it is still reachable, and the corner when it is not.
    ///
    /// <para>
    /// <paramref name="areas"/> is every screen's working area, with the primary one first;
    /// the fallback corner is measured from that first entry. A stored position is honoured
    /// as long as enough of the overlay lands on <em>any</em> of them, so a character parked
    /// on a second monitor stays there for as long as that monitor exists and comes home the
    /// moment it does not.
    /// </para>
    /// <para>
    /// Deliberately does not clamp a nearly-off-screen position back inside. Nudging Otto a
    /// few pixels would leave him somewhere the user did not choose and did not ask for,
    /// which is more confusing than the corner — the corner is at least where he started.
    /// </para>
    /// </summary>
    public static PixelPoint Resolve(PixelPoint? stored, PixelSize size, IReadOnlyList<PixelRect> areas)
    {
        // No screens at all is not a real desktop, but Screens.All can be empty while the
        // shell is still coming up. Answering with the stored point keeps this total, and
        // the window will be positioned again the next time anything moves it.
        if (areas.Count == 0) return stored ?? default;

        if (stored is not { } point) return Corner(areas[0], size);

        return IsReachable(point, size, areas) ? point : Corner(areas[0], size);
    }

    /// <summary>
    /// Whether enough of the overlay at <paramref name="position"/> lands on a screen to be
    /// grabbed.
    /// </summary>
    public static bool IsReachable(PixelPoint position, PixelSize size, IReadOnlyList<PixelRect> areas)
    {
        var overlay = new PixelRect(position, size);

        foreach (var area in areas)
        {
            // Measured per axis rather than by area: a strip 200 px wide and 2 px tall has
            // plenty of overlapping pixels and is still nothing anyone can aim at.
            var across = Math.Min(overlay.Right, area.Right) - Math.Max(overlay.X, area.X);
            var down = Math.Min(overlay.Bottom, area.Bottom) - Math.Max(overlay.Y, area.Y);

            if (across >= MinimumVisible && down >= MinimumVisible) return true;
        }

        return false;
    }
}

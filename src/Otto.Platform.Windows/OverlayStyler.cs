using Otto.Core;

namespace Otto.Platform.Windows;

/// <summary>
/// Applies the extended window styles that turn an ordinary window into a
/// non-intrusive overlay.
///
/// Each flag is load-bearing:
/// <list type="bullet">
/// <item><c>WS_EX_LAYERED</c> — required before a window can be transparent at all.</item>
/// <item><c>WS_EX_TRANSPARENT</c> — clicks pass through to whatever is underneath,
/// so the character never blocks the work it floats over.</item>
/// <item><c>WS_EX_TOOLWINDOW</c> — keeps it out of Alt+Tab and the taskbar. A
/// decorative sprite should not be something the user cycles through.</item>
/// <item><c>WS_EX_NOACTIVATE</c> — the important one. Without it the overlay can
/// take focus, and if it does that a moment before the transcription is pasted,
/// the text lands in Otto instead of in the user's document.</item>
/// </list>
/// </summary>
public sealed class OverlayStyler : IOverlayStyler
{
    public void MakeClickThrough(IntPtr windowHandle) =>
        Apply(windowHandle, Native.WS_EX_TRANSPARENT);

    /// <summary>
    /// Everything above except <c>WS_EX_TRANSPARENT</c>.
    ///
    /// <para>
    /// One flag apart, and it is the flag that decides whether a window is scenery or a
    /// control: with it, clicks land on whatever is underneath, which for a pause button
    /// means the button can never be pressed. <c>WS_EX_NOACTIVATE</c> is what makes the
    /// combination safe — the card can be clicked without the click moving focus, so the
    /// document the user was reading is still the foreground window when the reading ends
    /// and they go back to dictating into it.
    /// </para>
    /// </summary>
    public void MakeNonActivating(IntPtr windowHandle) => Apply(windowHandle, 0);

    private static void Apply(IntPtr windowHandle, long extra)
    {
        if (windowHandle == IntPtr.Zero) return;

        var current = Native.GetWindowLongPtr(windowHandle, Native.GWL_EXSTYLE).ToInt64();

        var updated = current
                      | Native.WS_EX_LAYERED
                      | Native.WS_EX_TOOLWINDOW
                      | Native.WS_EX_NOACTIVATE
                      | extra;

        Native.SetWindowLongPtr(windowHandle, Native.GWL_EXSTYLE, new IntPtr(updated));
    }
}

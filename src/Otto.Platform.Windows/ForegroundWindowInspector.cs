using System.Diagnostics;
using Otto.Core;

namespace Otto.Platform.Windows;

/// <summary>
/// Identifies the application the user is dictating into.
///
/// This drives two things: the format the text is post-processed into, and — per
/// milestone 0.5 — the `initial_prompt` used to transcribe it. The second one runs
/// before inference, which is why this has to be cheap.
/// </summary>
public sealed class ForegroundWindowInspector : IForegroundWindow
{
    public DictationContext Current()
    {
        var handle = Native.GetForegroundWindow();
        if (handle == IntPtr.Zero) return DictationContext.Unknown;

        return new DictationContext(ProcessName(handle), WindowTitle(handle));
    }

    private static string ProcessName(IntPtr handle)
    {
        if (Native.GetWindowThreadProcessId(handle, out var processId) == 0) return "";

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The window can disappear between the two calls. Not an error worth
            // surfacing — dictation just proceeds without context.
            return "";
        }
    }

    private static string WindowTitle(IntPtr handle)
    {
        var length = Native.GetWindowTextLength(handle);
        if (length <= 0) return "";

        var buffer = new char[length + 1];
        var written = Native.GetWindowText(handle, buffer, buffer.Length);

        return written > 0 ? new string(buffer, 0, written) : "";
    }
}

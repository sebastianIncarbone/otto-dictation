using System.Runtime.InteropServices;

namespace Otto.Platform.Windows;

/// <summary>
/// The raw Win32 clipboard, shared by everything in Otto that has to borrow it.
///
/// <para>
/// Extracted because there are now two of those and they pull in opposite directions:
/// <see cref="ClipboardTextInjector"/> puts a dictation on the clipboard to paste it into
/// the user's window, and <see cref="ClipboardSelectionReader"/> takes a copy of what the
/// user has selected so it can be read aloud. Both borrow, both must give back, and both
/// have to ask clipboard managers not to record what passed through. One copy of that is
/// the only way the two stay honest.
/// </para>
/// <para>
/// The raw Win32 clipboard rather than Avalonia's, on purpose: this has no apartment
/// requirement, which is why dictation kept working during the bug where copying a note
/// through the OLE-backed clipboard killed the app.
/// </para>
/// <para>
/// Static, with no logging. Every method answers with a value rather than a thrown
/// exception, and the callers are the ones with the context to say what a failure meant.
/// </para>
/// </summary>
internal static class WindowsClipboard
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(20);
    private const int OpenAttempts = 10;

    /// <summary>
    /// Advisory formats. Well-behaved clipboard managers honour them; others do not,
    /// which is a residual limitation worth documenting rather than hiding.
    ///
    /// <para>
    /// Registered once for the process. <c>RegisterClipboardFormat</c> answers with the
    /// same id for the same name every time, so a second registration would buy nothing.
    /// </para>
    /// </summary>
    private static readonly uint[] ExclusionFormats =
    [
        Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"),
        Native.RegisterClipboardFormat("CanIncludeInClipboardHistory"),
        Native.RegisterClipboardFormat("CanUploadToCloudClipboard"),
    ];

    public static string? TryReadText()
    {
        if (!TryOpen()) return null;

        try
        {
            var handle = Native.GetClipboardData(Native.CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return null;

            var pointer = Native.GlobalLock(handle);
            if (pointer == IntPtr.Zero) return null;

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                Native.GlobalUnlock(handle);
            }
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    public static bool TryWriteText(string text, bool excludeFromHistory = true)
    {
        if (!TryOpen()) return false;

        try
        {
            Native.EmptyClipboard();

            var handle = AllocateUnicode(text);
            if (handle == IntPtr.Zero) return false;

            // Ownership transfers to the system on success; on failure it is still ours
            // and has to be released.
            if (Native.SetClipboardData(Native.CF_UNICODETEXT, handle) == IntPtr.Zero)
            {
                Native.GlobalFree(handle);
                return false;
            }

            if (excludeFromHistory) ApplyExclusionFormats();

            return true;
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    /// <summary>
    /// Leaves the clipboard empty — what "put it back" means when the user had nothing
    /// there to begin with.
    /// </summary>
    public static bool TryClear()
    {
        if (!TryOpen()) return false;

        try
        {
            return Native.EmptyClipboard();
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    /// <summary>
    /// The exclusion formats carry no payload — their presence on the clipboard is the
    /// whole signal. A single zero byte is enough to register them.
    /// </summary>
    private static void ApplyExclusionFormats()
    {
        foreach (var format in ExclusionFormats)
        {
            if (format == 0) continue;

            var marker = Native.GlobalAlloc(Native.GMEM_MOVEABLE, 1);
            if (marker == IntPtr.Zero) continue;

            if (Native.SetClipboardData(format, marker) == IntPtr.Zero)
                Native.GlobalFree(marker);
        }
    }

    private static IntPtr AllocateUnicode(string text)
    {
        var bytes = (nuint)((text.Length + 1) * sizeof(char));

        var handle = Native.GlobalAlloc(Native.GMEM_MOVEABLE, bytes);
        if (handle == IntPtr.Zero) return IntPtr.Zero;

        var pointer = Native.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            Native.GlobalFree(handle);
            return IntPtr.Zero;
        }

        try
        {
            Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
            Marshal.WriteInt16(pointer, text.Length * sizeof(char), 0);
        }
        finally
        {
            Native.GlobalUnlock(handle);
        }

        return handle;
    }

    /// <summary>
    /// The clipboard is a single system-wide resource and any application may hold it for
    /// a moment, so failing to open it once means nothing.
    /// </summary>
    private static bool TryOpen()
    {
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (Native.OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(RetryDelay);
        }

        return false;
    }
}

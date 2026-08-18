using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Otto.Core;

namespace Otto.Platform.Windows;

/// <summary>
/// Writes the transcription into the focused window by putting it on the clipboard
/// and sending Ctrl+V.
///
/// <para>
/// Chosen over typing the text with <c>SendInput</c> because synthesising one key
/// event per character is slow for a paragraph and several applications — terminals
/// and Electron apps in particular — drop or reorder the events.
/// </para>
/// <para>
/// Two obligations come with borrowing the clipboard, and the second one is the one
/// that is usually missed:
/// </para>
/// <list type="number">
/// <item>Put back whatever the user had. Best effort: text is restored exactly,
/// other formats cannot always be round-tripped.</item>
/// <item>Ask clipboard managers not to record the dictation at all. Restoring is
/// not enough — Windows Clipboard History and third-party managers capture the
/// moment the clipboard changes, so without this every dictated sentence ends up
/// in a history the user never asked for. For a tool whose entire promise is that
/// the audio never leaves the machine, that would be the wrong outcome.</item>
/// </list>
/// </summary>
public sealed class ClipboardTextInjector : ITextInjector
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(20);
    private const int OpenAttempts = 10;

    private readonly ILogger<ClipboardTextInjector> log;
    private readonly uint[] exclusionFormats;

    public ClipboardTextInjector(ILogger<ClipboardTextInjector> log)
    {
        this.log = log;

        // Advisory formats. Well-behaved clipboard managers honour them; others do
        // not, which is a residual limitation worth documenting rather than hiding.
        exclusionFormats =
        [
            Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"),
            Native.RegisterClipboardFormat("CanIncludeInClipboardHistory"),
            Native.RegisterClipboardFormat("CanUploadToCloudClipboard"),
        ];
    }

    public async Task InjectAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text)) return;

        var saved = TryReadClipboardText();

        if (!TryWriteClipboard(text))
        {
            log.LogWarning("Could not write to the clipboard; the injection is cancelled");
            return;
        }

        SendPaste();

        // The target application needs a moment to service the paste before the
        // clipboard is handed back, otherwise it pastes the restored content.
        await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken);

        if (saved is not null && !TryWriteClipboard(saved, excludeFromHistory: false))
            log.LogWarning("Could not restore the previous clipboard contents");
    }

    private string? TryReadClipboardText()
    {
        if (!TryOpenClipboard()) return null;

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

    private bool TryWriteClipboard(string text, bool excludeFromHistory = true)
    {
        if (!TryOpenClipboard()) return false;

        try
        {
            Native.EmptyClipboard();

            var handle = AllocateUnicode(text);
            if (handle == IntPtr.Zero) return false;

            // Ownership transfers to the system on success; on failure it is still
            // ours and has to be released.
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
    /// The exclusion formats carry no payload — their presence on the clipboard is
    /// the whole signal. A single zero byte is enough to register them.
    /// </summary>
    private void ApplyExclusionFormats()
    {
        foreach (var format in exclusionFormats)
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
    /// The clipboard is a single system-wide resource and any application may hold
    /// it for a moment, so failing to open it once means nothing.
    /// </summary>
    private bool TryOpenClipboard()
    {
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (Native.OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(RetryDelay);
        }

        return false;
    }

    private static void SendPaste()
    {
        var size = Marshal.SizeOf<Native.INPUT>();

        Native.INPUT Key(ushort vk, bool up) => new()
        {
            type = Native.INPUT_KEYBOARD,
            ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = up ? Native.KEYEVENTF_KEYUP : 0 },
        };

        Native.INPUT[] sequence =
        [
            Key(Native.VK_CONTROL, up: false),
            Key(Native.VK_V, up: false),
            Key(Native.VK_V, up: true),
            Key(Native.VK_CONTROL, up: true),
        ];

        Native.SendInput((uint)sequence.Length, sequence, size);
    }
}

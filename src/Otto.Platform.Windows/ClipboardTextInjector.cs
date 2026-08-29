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
/// <para>
/// The Win32 calls themselves live in <see cref="WindowsClipboard"/>, shared with
/// <see cref="ClipboardSelectionReader"/>, which has the same two obligations pointing
/// the other way.
/// </para>
/// </summary>
public sealed class ClipboardTextInjector(ILogger<ClipboardTextInjector> log) : ITextInjector
{
    public async Task InjectAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text)) return;

        var saved = WindowsClipboard.TryReadText();

        if (!WindowsClipboard.TryWriteText(text))
        {
            log.LogWarning("Could not write to the clipboard; the injection is cancelled");
            return;
        }

        SendPaste();

        // The target application needs a moment to service the paste before the
        // clipboard is handed back, otherwise it pastes the restored content.
        await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken);

        if (saved is not null && !WindowsClipboard.TryWriteText(saved, excludeFromHistory: false))
            log.LogWarning("Could not restore the previous clipboard contents");
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

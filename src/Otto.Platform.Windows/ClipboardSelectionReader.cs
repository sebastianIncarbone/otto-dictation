using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Otto.Core;

namespace Otto.Platform.Windows;

/// <summary>
/// Gets the text to read by copying whatever the user has selected, and falling back to
/// the clipboard when nothing is.
///
/// <para>
/// This is what collapses two hotkeys into one. "Read my selection" and "read what I
/// copied" were separate features on paper; in practice a synthetic Ctrl+C answers both,
/// because an application with nothing selected leaves the clipboard alone. So: remember
/// the clipboard, press Ctrl+C on the user's behalf, and see whether anything changed.
/// Changed means there was a selection and that is what gets read; unchanged means there
/// was not, and what the user copied earlier gets read instead. Neither outcome is a
/// surprise, and there is one key to remember.
/// </para>
/// <para>
/// Then the clipboard goes back exactly as it was, including being emptied when it
/// started empty. The user asked for a reading, not for their clipboard to be replaced
/// by whatever was on screen.
/// </para>
/// <para>
/// <b>Residual limitation, stated rather than hidden.</b> The copy is performed by the
/// source application, not by Otto, so the exclusion formats
/// <see cref="ClipboardTextInjector"/> relies on cannot be applied to it — a clipboard
/// manager may still record the selection. Restoring the clipboard afterwards does not
/// undo that. It is the unavoidable cost of reaching a selection at all on Windows
/// without a UI Automation dependency, and it is worth knowing before assuming this
/// leaves no trace.
/// </para>
/// </summary>
public sealed class ClipboardSelectionReader(ILogger<ClipboardSelectionReader> log) : ISelectionReader
{
    /// <summary>
    /// How long the source application gets to service the synthetic Ctrl+C.
    ///
    /// <para>
    /// Polled rather than slept through, because the spread is wide: a text editor
    /// answers immediately, a browser or an Electron app can take a few hundred
    /// milliseconds. A fixed delay would have to be the worst case, and that delay lands
    /// squarely in front of the first word — the one wait in this whole feature the user
    /// actually sits through.
    /// </para>
    /// </summary>
    private static readonly TimeSpan CopyTimeout = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// How long to wait for the user to let go of the hotkey before copying.
    ///
    /// <para>
    /// Bounded rather than unbounded because a key can be reported down forever — a stuck
    /// modifier, a remote desktop session that lost a keyup — and a reading feature that
    /// waits for a key that will never come back up is indistinguishable from a broken
    /// one. Past this, the copy is attempted anyway: it will probably fail, and failing
    /// falls back to reading the clipboard, which is a far better outcome than silence.
    /// </para>
    /// </summary>
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(2);

    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        await WaitForModifiersAsync(cancellationToken);

        var saved = WindowsClipboard.TryReadText();

        SendCopy();

        var copied = await WaitForCopyAsync(saved, cancellationToken);

        if (copied is null)
        {
            // Nothing was selected, so the clipboard was never touched and there is
            // nothing to put back. Whatever the user copied earlier is what gets read.
            log.LogDebug("Nothing was selected; reading the clipboard instead");
            return saved;
        }

        Restore(saved);

        return copied;
    }

    /// <summary>
    /// Returns the newly copied text, or null if the clipboard never changed.
    ///
    /// <para>
    /// A selection identical to what was already on the clipboard is indistinguishable
    /// from no selection at all, and reads as the latter. It costs the full timeout to
    /// decide, and produces exactly the same text either way — which is why this is worth
    /// a sentence rather than a fix.
    /// </para>
    /// </summary>
    private static async Task<string?> WaitForCopyAsync(string? before, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + CopyTimeout;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken);

            var now = WindowsClipboard.TryReadText();

            if (now is not null && now != before) return now;
        }

        return null;
    }

    /// <summary>
    /// Waits until the user is off every modifier, and this is not politeness.
    ///
    /// <para>
    /// Synthetic input is merged with the real keyboard state rather than replacing it.
    /// The reading hotkey is Ctrl+Alt+L, so at the moment its handler runs the user is
    /// still holding Ctrl and Alt — and a Ctrl+C sent right then arrives at the target
    /// application as <b>Ctrl+Alt+C</b>, which almost nothing treats as copy. The
    /// clipboard would never change, every reading would silently fall back to whatever
    /// was copied earlier, and the selection the user actually pointed at would never be
    /// read once.
    /// </para>
    /// <para>
    /// Waiting is the fix rather than synthesising key-ups for the modifiers: the user's
    /// physical keys are still down, so the released state would be contradicted the
    /// moment the OS next samples the keyboard, and the target application would have
    /// seen a keyup its user never performed. A tap releases in well under a tenth of a
    /// second, so the cost is not something anyone perceives.
    /// </para>
    /// </summary>
    private async Task WaitForModifiersAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ReleaseTimeout;

        while (AnyModifierDown())
        {
            if (DateTime.UtcNow >= deadline)
            {
                log.LogWarning("A modifier key is still down after {Seconds:N0} s; copying anyway",
                    ReleaseTimeout.TotalSeconds);
                return;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private static bool AnyModifierDown() =>
        IsDown(Native.VK_CONTROL) || IsDown(Native.VK_MENU) ||
        IsDown(Native.VK_SHIFT) || IsDown(Native.VK_LWIN) || IsDown(Native.VK_RWIN);

    /// <summary>The high bit of GetAsyncKeyState is the "currently down" flag.</summary>
    private static bool IsDown(ushort virtualKey) => (Native.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private void Restore(string? saved)
    {
        // Emptying is the honest restore when the user had nothing. Leaving the copied
        // selection behind would mean this feature quietly changed their clipboard, which
        // is precisely what it is supposed not to do.
        var restored = saved is null
            ? WindowsClipboard.TryClear()
            : WindowsClipboard.TryWriteText(saved, excludeFromHistory: false);

        if (!restored) log.LogWarning("Could not restore the previous clipboard contents");
    }

    private static void SendCopy()
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
            Key(Native.VK_C, up: false),
            Key(Native.VK_C, up: true),
            Key(Native.VK_CONTROL, up: true),
        ];

        Native.SendInput((uint)sequence.Length, sequence, size);
    }
}

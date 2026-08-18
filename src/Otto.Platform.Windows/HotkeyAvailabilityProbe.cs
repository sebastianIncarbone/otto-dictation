using System.Runtime.InteropServices;
using Otto.Core;

namespace Otto.Platform.Windows;

/// <summary>
/// Answers <see cref="IHotkeyAvailability.IsAvailable"/> by briefly, actually
/// registering the binding — the only way to ask Windows the truth, since a
/// hardcoded reserved-key list would say nothing about what some other running
/// application already holds. A new file rather than an addition to
/// <see cref="PollingHotkeyService"/>: sharing that file buys nothing here and
/// would put its CTS-reuse landmine within reach of unrelated code. Its own
/// hotkey id and its own throwaway thread mean it can never disturb whatever
/// <see cref="PollingHotkeyService"/> currently has registered.
/// </summary>
public sealed class HotkeyAvailabilityProbe : IHotkeyAvailability
{
    // Distinct from PollingHotkeyService.HotkeyId (1) so the two can never
    // collide even by coincidence while reading either file on its own.
    private const int ProbeHotkeyId = 2;

    // RegisterHotKey/PeekMessage/UnregisterHotKey are plain synchronous syscalls
    // with no I/O — the whole round trip normally completes in well under a
    // millisecond. 2 seconds was a UI freeze waiting to happen: IsAvailable is
    // called synchronously from OfferKey on the Avalonia UI thread, so that
    // budget only ever gets spent when something is already wrong, and it froze
    // the settings window with no feedback for the whole 2 seconds. A short
    // timeout is safe specifically because the port's contract already answers
    // "available" whenever it cannot tell — timing out early only costs an
    // occasional false "available" (a warning the user never sees because the
    // combination was fine), never a wrong refusal.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(200);

    public bool IsAvailable(HotkeyBinding binding)
    {
        // Starts true so a probe that never finishes in time — or fails for any
        // reason at all — is optimistic by construction: the port's own contract
        // is to answer "available" whenever it genuinely cannot tell, and a
        // crash would satisfy neither branch of that obligation.
        var available = true;

        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    Probe(binding, out available);
                }
                catch (Exception)
                {
                    // Guards the native calls themselves: an exception escaping
                    // this delegate on a background thread with no handler would
                    // take the whole process down, which is strictly worse than
                    // the wrong-but-safe "available" answer this falls back to.
                    available = true;
                }
            })
            {
                IsBackground = true,
                Name = "Otto.HotkeyProbe",
            };

            // RegisterHotKey/UnregisterHotKey are scoped to the calling thread's
            // own message queue, so both have to run on this one dedicated thread.
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(ProbeTimeout);
        }
        catch (Exception)
        {
            // Same obligation, covering the thread-start path too: a failure to
            // even spin up the probe thread (e.g. Thread.Start throwing) must
            // fall back to optimistic, not propagate out of IsAvailable.
            available = true;
        }

        return available;
    }

    private static void Probe(HotkeyBinding binding, out bool available)
    {
        // Forces Windows to create the queue before RegisterHotKey targets it —
        // without this the call can fail spuriously, same as PollingHotkeyService.
        Native.PeekMessage(out _, IntPtr.Zero, 0, 0, Native.PM_REMOVE);

        var modifiers = (uint)binding.Modifiers | Native.MOD_NOREPEAT;

        if (!Native.RegisterHotKey(IntPtr.Zero, ProbeHotkeyId, modifiers, binding.VirtualKey))
        {
            // 1409 (ERROR_HOTKEY_ALREADY_REGISTERED) is the one refusal that means
            // "taken"; anything else stays optimistic rather than blocking a
            // binding for an unrelated cause.
            available = Marshal.GetLastWin32Error() != 1409;
            return;
        }

        try
        {
            available = true;
        }
        finally
        {
            // The obligation that makes this probe safe: never leave a
            // system-global combination held — a leak would make Otto itself
            // the app blocking a perfectly good binding on the next attempt.
            Native.UnregisterHotKey(IntPtr.Zero, ProbeHotkeyId);
        }
    }
}

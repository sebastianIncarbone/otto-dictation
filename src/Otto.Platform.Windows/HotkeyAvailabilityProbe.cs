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
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    public bool IsAvailable(HotkeyBinding binding)
    {
        // Starts true so a probe that never finishes in time is optimistic by
        // construction — it must never block a binding it genuinely cannot vouch for.
        var available = true;

        var thread = new Thread(() => Probe(binding, out available))
        {
            IsBackground = true,
            Name = "Otto.HotkeyProbe",
        };

        // RegisterHotKey/UnregisterHotKey are scoped to the calling thread's own
        // message queue, so both have to run on this one dedicated thread.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(ProbeTimeout);

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

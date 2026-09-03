using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Otto.Core;

namespace Otto.Platform.Windows;

/// <summary>
/// A global hotkey that fires on press and has nothing to say about release.
///
/// <para>
/// Deliberately not a second consumer of <see cref="PollingHotkeyService"/>, and the
/// difference is the whole reason this file exists. Dictation is push-to-talk, so that
/// service has to answer the question <c>RegisterHotKey</c> never will — has the key been
/// let go? — by spinning <c>GetAsyncKeyState</c> until it has. Reading is a tap. There is
/// no hold, and a release event would mean nothing, so reusing that service would mean
/// starting a polling loop on every press purely to produce an event this feature
/// discards.
/// </para>
/// <para>
/// It also cannot simply register a second binding on the existing service: with a null
/// window handle, <c>RegisterHotKey</c> binds the hotkey to the calling thread's message
/// queue, and that service owns exactly one thread with one pump and one id. A second
/// thread with its own pump is both simpler and correct — hotkey ids only have to be
/// unique per thread, so the two cannot collide.
/// </para>
/// </summary>
public sealed class SingleShotHotkeyService(ILogger<SingleShotHotkeyService> log) : ISingleShotHotkey
{
    private const int HotkeyId = 2;

    private Thread? pump;
    private uint pumpThreadId;

    public event Action? Pressed;

    public void Register(HotkeyBinding requested)
    {
        if (pump is not null) throw new InvalidOperationException("The reading hotkey is already registered.");

        // WM_HOTKEY is delivered to the thread's message queue, not to a window, so the
        // whole thing lives on one dedicated thread with its own pump.
        var ready = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        pump = new Thread(() => RunPump(requested, ready))
        {
            IsBackground = true,
            Name = "Otto.ReadingHotkey",
        };

        pump.SetApartmentState(ApartmentState.STA);
        pump.Start();

        if (ready.Task.GetAwaiter().GetResult() is { } failure)
        {
            // Left non-null, a failed registration would make every later attempt throw
            // "already registered" instead of retrying — which is exactly what a user
            // does after freeing up the combination that was taken.
            pump = null;
            throw failure;
        }
    }

    private void RunPump(HotkeyBinding requested, TaskCompletionSource<Exception?> ready)
    {
        pumpThreadId = Native.GetCurrentThreadId();

        // Touching the queue with PeekMessage forces Windows to create it before
        // RegisterHotKey targets it. Without this the first press can be lost.
        Native.PeekMessage(out _, IntPtr.Zero, 0, 0, Native.PM_REMOVE);

        var modifiers = (uint)requested.Modifiers | Native.MOD_NOREPEAT;

        if (!Native.RegisterHotKey(IntPtr.Zero, HotkeyId, modifiers, requested.VirtualKey))
        {
            var error = Marshal.GetLastWin32Error();
            ready.SetResult(new HotkeyRegistrationException(requested, alreadyInUse: error == 1409));
            return;
        }

        log.LogInformation("Reading hotkey registered: {Modifiers}+0x{Key:X2}", requested.Modifiers, requested.VirtualKey);
        ready.SetResult(null);

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message != Native.WM_HOTKEY || msg.wParam.ToInt32() != HotkeyId) continue;

            // No guard against a second press arriving while the first is still being
            // handled, and that is the point: pressing again is how a reading is stopped.
            // ReadingPipeline owns that decision — it is the layer that knows whether one
            // is in progress — so this stays a plain "the key was pressed" signal.
            Pressed?.Invoke();
        }

        Native.UnregisterHotKey(IntPtr.Zero, HotkeyId);
    }

    public void Unregister()
    {
        if (pump is null) return;

        Native.PostThreadMessage(pumpThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        pump.Join(TimeSpan.FromSeconds(2));
        pump = null;
    }

    public void Dispose() => Unregister();
}

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Otto.Core;

namespace Otto.Platform.Windows;

/// <summary>
/// Push-to-talk built on <c>RegisterHotKey</c>.
///
/// <para>
/// The catch that the design has to work around: <c>RegisterHotKey</c> posts
/// <c>WM_HOTKEY</c> when the combination is <b>pressed</b> and sends nothing at all
/// when it is released. On its own it can start a recording but never stop one.
/// </para>
/// <para>
/// So the release is detected by polling <c>GetAsyncKeyState</c> after the press.
/// The alternative — a <c>WH_KEYBOARD_LL</c> hook — gives real release events and
/// supports modifier-only bindings, but installs a system-wide keyboard hook, which
/// is structurally a keylogger and draws antivirus attention. This implementation
/// is the default precisely because it installs nothing.
/// </para>
/// <para>
/// Consequence of the trade-off: the binding must include a non-modifier key. You
/// cannot bind "hold Right Ctrl" here.
/// </para>
/// </summary>
public sealed class PollingHotkeyService : IHotkeyService
{
    private const int HotkeyId = 1;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(15);

    private readonly ILogger<PollingHotkeyService> log;
    private readonly CancellationTokenSource shutdown = new();

    private Thread? pump;
    private uint pumpThreadId;
    private HotkeyBinding? binding;
    private int recording;

    public PollingHotkeyService(ILogger<PollingHotkeyService> log) => this.log = log;

    public event Action? Pressed;
    public event Action? Released;

    public void Register(HotkeyBinding requested)
    {
        if (pump is not null) throw new InvalidOperationException("The hotkey is already registered.");

        binding = requested;

        // WM_HOTKEY is delivered to the thread's message queue, not to a window, so
        // the whole thing lives on one dedicated thread with its own pump. That
        // also keeps it independent of whether the host is a console or a GUI app.
        var ready = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        pump = new Thread(() => RunPump(requested, ready))
        {
            IsBackground = true,
            Name = "Otto.Hotkey",
        };

        pump.SetApartmentState(ApartmentState.STA);
        pump.Start();

        if (ready.Task.GetAwaiter().GetResult() is { } failure) throw failure;
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
            ready.SetResult(new InvalidOperationException(
                error == 1409
                    ? "That combination is already in use by another application. Pick a different one."
                    : $"Could not register the hotkey (error {error})."));
            return;
        }

        log.LogInformation("Hotkey registered: {Modifiers}+0x{Key:X2}", requested.Modifiers, requested.VirtualKey);
        ready.SetResult(null);

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == Native.WM_HOTKEY && msg.wParam.ToInt32() == HotkeyId)
                OnHotkeyPressed();
        }

        Native.UnregisterHotKey(IntPtr.Zero, HotkeyId);
    }

    private void OnHotkeyPressed()
    {
        // MOD_NOREPEAT covers auto-repeat, but a second physical press arriving
        // while the previous dictation is still being polled would double-start.
        if (Interlocked.Exchange(ref recording, 1) == 1) return;

        Pressed?.Invoke();

        // Polling runs off the pump so the message loop stays responsive and can
        // still receive WM_QUIT while the user is holding the key.
        _ = Task.Run(WaitForReleaseAsync);
    }

    private async Task WaitForReleaseAsync()
    {
        try
        {
            var key = binding!.VirtualKey;

            while (!shutdown.IsCancellationRequested && IsDown(key))
                await Task.Delay(PollInterval, shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-dictation; still fall through so the recording is
            // closed rather than left running.
        }
        finally
        {
            Interlocked.Exchange(ref recording, 0);
            Released?.Invoke();
        }
    }

    /// <summary>The high bit of GetAsyncKeyState is the "currently down" flag.</summary>
    private static bool IsDown(uint virtualKey) => (Native.GetAsyncKeyState((int)virtualKey) & 0x8000) != 0;

    public void Unregister()
    {
        if (pump is null) return;

        shutdown.Cancel();
        Native.PostThreadMessage(pumpThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        pump.Join(TimeSpan.FromSeconds(2));
        pump = null;
    }

    public void Dispose()
    {
        Unregister();
        shutdown.Dispose();
    }
}

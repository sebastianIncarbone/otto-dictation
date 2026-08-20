using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace Otto.App;

/// <summary>
/// The last line of defence: whatever escapes everything else gets written down
/// before Otto is gone.
///
/// <para>
/// This is the same policy <c>DictationPipeline</c> already applies to itself —
/// log the exception, swallow it, carry on — extended to the surfaces the
/// pipeline does not own. A background dictation tool that dies on one bad
/// click leaves the user with nothing running and no idea why, which is exactly
/// what happened when copying a note threw on the UI thread.
/// </para>
/// </summary>
public static class Crash
{
    /// <summary>
    /// Installed before the service provider is built, so a failure in the GPU
    /// probe or the settings load — everything that happens before there is an
    /// <see cref="ILogger"/> to fail into — is still recorded. Writes straight
    /// to the file for the same reason.
    /// </summary>
    public static void Install(LogFile log)
    {
        // Nothing can stop the process here; the CLR is already unwinding. The
        // only job left is to leave a record, and IsTerminating says whether the
        // next thing the user sees is Otto disappearing.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var message = e.IsTerminating
                ? "Unhandled exception; the process is terminating"
                : "Unhandled exception on a background thread";

            log.Write(LogLevel.Critical, "Otto.App.Crash", message, e.ExceptionObject as Exception);
        };

        // A faulted Task nobody awaited. Harmless to the process since .NET 4.5,
        // but it is how a fire-and-forget path fails silently — and the pipeline
        // fires its work without awaiting it on purpose, so this is where that
        // would show up.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Write(LogLevel.Error, "Otto.App.Crash", "Faulted task that nobody observed", e.Exception);

            // Marking it observed changes nothing about the process; it only
            // stops the runtime treating the finalizer-thread report as news.
            e.SetObserved();
        };
    }

    /// <summary>
    /// The UI thread, which is the one that matters: an exception thrown inside a
    /// command handler or an event handler surfaces here with nothing above it,
    /// and unhandled it takes the whole tray app down.
    ///
    /// <para>
    /// Marking it handled is a deliberate trade. A swallowed exception can leave
    /// a screen in a state its author did not plan for, and that is a real cost.
    /// It is still the smaller one: Otto is a background tool whose value is
    /// being there when the hotkey is pressed, and no failure in the notes window
    /// is worth ending the dictation service the user actually launched. The same
    /// reasoning as <c>DictationPipeline</c> swallowing a bad transcription.
    /// </para>
    ///
    /// <para>
    /// Installed once the framework is up, because <see cref="Dispatcher.UIThread"/>
    /// does not exist before Avalonia has initialised the platform.
    /// </para>
    /// </summary>
    public static void InstallUiHandler(ILogger log)
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            log.LogError(e.Exception, "Unhandled exception on the UI thread; Otto keeps running");
            e.Handled = true;
        };
    }
}

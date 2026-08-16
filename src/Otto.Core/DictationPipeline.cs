using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Otto.Core;

public enum DictationState { Loading, Idle, Recording, Transcribing }

/// <summary>
/// The whole product, in one class: hold the key, talk, release, see text.
///
/// Everything platform-specific is behind a port, so this can be exercised end to
/// end without a microphone, a GPU or a foreground window.
/// </summary>
public sealed class DictationPipeline : IDisposable
{
    private readonly IHotkeyService hotkey;
    private readonly IAudioCapture capture;
    private readonly ITranscriber transcriber;
    private readonly ITextInjector injector;
    private readonly IForegroundWindow foreground;
    private readonly ILogger<DictationPipeline> log;

    private DictationContext pending = DictationContext.Unknown;
    private int busy;

    public DictationPipeline(
        IHotkeyService hotkey,
        IAudioCapture capture,
        ITranscriber transcriber,
        ITextInjector injector,
        IForegroundWindow foreground,
        ILogger<DictationPipeline> log)
    {
        this.hotkey = hotkey;
        this.capture = capture;
        this.transcriber = transcriber;
        this.injector = injector;
        this.foreground = foreground;
        this.log = log;
    }

    public DictationState State { get; private set; } = DictationState.Loading;

    public event Action<DictationState>? StateChanged;

    /// <summary>Fired after each dictation so the history can persist it (milestone 2).</summary>
    public event Action<string, DictationContext>? Dictated;

    public async Task StartAsync(HotkeyBinding binding, CancellationToken cancellationToken = default)
    {
        Transition(DictationState.Loading);

        var watch = Stopwatch.StartNew();
        await transcriber.LoadAsync(cancellationToken);
        log.LogInformation("Modelo cargado en {Seconds:F1} s", watch.Elapsed.TotalSeconds);

        hotkey.Pressed += OnPressed;
        hotkey.Released += OnReleased;
        hotkey.Register(binding);

        Transition(DictationState.Idle);
    }

    private void OnPressed()
    {
        if (State != DictationState.Idle) return;

        // Captured on press, not on release: by the time the text is injected the
        // user may have switched windows, and the prompt has to match where they
        // were speaking from.
        pending = foreground.Current();

        capture.Start();
        Transition(DictationState.Recording);
    }

    private void OnReleased()
    {
        if (State != DictationState.Recording) return;

        var audio = capture.Stop();
        Transition(DictationState.Transcribing);

        // Deliberately not awaited: the hotkey callback runs on the message loop,
        // and blocking it would freeze every other hotkey in the system.
        _ = TranscribeAndInjectAsync(audio, pending);
    }

    private async Task TranscribeAndInjectAsync(AudioBuffer audio, DictationContext context)
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;

        var total = Stopwatch.StartNew();

        try
        {
            var watch = Stopwatch.StartNew();
            var text = await transcriber.TranscribeAsync(audio, context);
            var transcribed = watch.Elapsed;

            if (string.IsNullOrWhiteSpace(text))
            {
                log.LogInformation("Sin voz detectada, nada que escribir");
                return;
            }

            watch.Restart();
            await injector.InjectAsync(text);

            log.LogInformation(
                "{Audio:F1} s de audio → {Chars} caracteres · transcripción {Transcribe:F2} s · inyección {Inject:F2} s · total {Total:F2} s",
                audio.Duration.TotalSeconds, text.Length,
                transcribed.TotalSeconds, watch.Elapsed.TotalSeconds, total.Elapsed.TotalSeconds);

            Dictated?.Invoke(text, context);
        }
        catch (Exception ex)
        {
            // A failed dictation must never take the app down: the user is in the
            // middle of typing somewhere else and would lose a background process
            // without ever seeing why.
            log.LogError(ex, "Falló el dictado");
        }
        finally
        {
            Interlocked.Exchange(ref busy, 0);
            Transition(DictationState.Idle);
        }
    }

    private void Transition(DictationState next)
    {
        if (State == next) return;

        State = next;
        StateChanged?.Invoke(next);
    }

    public void Dispose()
    {
        hotkey.Pressed -= OnPressed;
        hotkey.Released -= OnReleased;
        hotkey.Dispose();
        capture.Dispose();
    }
}

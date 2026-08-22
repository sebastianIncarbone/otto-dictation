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
    private readonly INoteRepository notes;
    private readonly IPostProcessor postProcessor;
    private readonly ILogger<DictationPipeline> log;

    private DictationContext pending = DictationContext.Unknown;
    private int busy;

    public DictationPipeline(
        IHotkeyService hotkey,
        IAudioCapture capture,
        ITranscriber transcriber,
        ITextInjector injector,
        IForegroundWindow foreground,
        INoteRepository notes,
        IPostProcessor postProcessor,
        ILogger<DictationPipeline> log)
    {
        this.hotkey = hotkey;
        this.capture = capture;
        this.transcriber = transcriber;
        this.injector = injector;
        this.foreground = foreground;
        this.notes = notes;
        this.postProcessor = postProcessor;
        this.log = log;
    }

    public DictationState State { get; private set; } = DictationState.Loading;

    public event Action<DictationState>? StateChanged;

    /// <summary>Fired as soon as the text is in the user's window.</summary>
    public event Action<string, DictationContext>? Dictated;

    /// <summary>Fired once the note is on disk, which happens after injection.</summary>
    public event Action<Note>? Saved;

    /// <summary>
    /// Fired once the deferred correction-model load kicked off by
    /// <see cref="StartAsync"/> settles — successfully or not. Lets a caller (the
    /// tray, the notes window) update its own state without polling
    /// <see cref="IPostProcessor.IsAvailable"/>.
    /// </summary>
    public event Action? CorrectionAvailabilityChanged;

    /// <summary>
    /// Fired when the key was held but nothing was said.
    ///
    /// Distinct from a failure: nothing went wrong, there was simply no speech. It
    /// exists because that case is invisible otherwise — the user holds the key,
    /// talks to a muted microphone, releases, and nothing at all happens. Somebody
    /// has to be able to say so.
    /// </summary>
    public event Action? HeardNothing;

    /// <summary>
    /// What Otto actually registered with Windows, as opposed to whatever is currently
    /// sitting in the settings editor. Null until <see cref="StartAsync"/> has
    /// successfully called <see cref="IHotkeyService.Register"/> — including while a
    /// failed registration has left the pipeline stuck in <see cref="DictationState.Loading"/>.
    /// This is the only source of truth the UI is allowed to read for "what is Otto
    /// listening on right now"; the settings editor's pending value must never be
    /// confusable with it.
    /// </summary>
    public HotkeyBinding? RegisteredHotkey { get; private set; }

    public async Task StartAsync(HotkeyBinding binding, CancellationToken cancellationToken = default)
    {
        Transition(DictationState.Loading);

        var watch = Stopwatch.StartNew();
        await transcriber.LoadAsync(cancellationToken);
        log.LogInformation("Model loaded in {Seconds:F1} s", watch.Elapsed.TotalSeconds);

        hotkey.Pressed += OnPressed;
        hotkey.Released += OnReleased;
        hotkey.Register(binding);
        RegisteredHotkey = binding;

        Transition(DictationState.Idle);

        // Deferred, not awaited: the point below — probed once at startup rather
        // than on every dictation, because a health check in the hot path would
        // cost more than the correction it guards — was always about not probing
        // per dictation, never about blocking hotkey readiness on it. Awaiting it
        // here would add the corrector's own load time (unmeasured, plausibly
        // several seconds cold) to every single launch of an autostart tray app.
        // Dictation is already fully usable on raw Whisper output the moment
        // Idle is reached above; a correction landing a few seconds later is
        // Otto's documented "everything optional degrades to nothing", not a
        // wait imposed on the user.
        _ = LoadCorrectorAsync(cancellationToken);
    }

    private async Task LoadCorrectorAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Enabled first: IPostProcessor is now ALWAYS the real corrector
            // on GPU hardware regardless of Settings.CorrectVoseo — see
            // Program.cs's own comment on why the DI decision had to stop
            // depending on that toggle, now that it can flip at runtime.
            // Loading a ~2 GB model into VRAM here for a feature the user
            // switched off would be exactly the startup cost this toggle
            // exists to let someone avoid; SetEnabledAsync is what loads it
            // later if they turn it back on.
            //
            // Asked once at startup rather than on every dictation: a health
            // check in the hot path would cost more than the correction it
            // guards.
            if (postProcessor.Enabled)
                await postProcessor.ProbeAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // postProcessor.ProbeAsync is documented never to throw — every known
            // implementation swallows its own failure into IsAvailable == false —
            // but this runs detached from StartAsync's caller, so nothing else
            // would ever observe an exception here. The same rule as everywhere
            // else in this class: a failure in the optional half must never cost
            // the user their already-working dictation.
            log.LogWarning(ex, "Could not load the correction model; dictation continues with raw text");
        }
        finally
        {
            CorrectionAvailabilityChanged?.Invoke();
        }
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
                log.LogInformation("No speech detected, nothing to write");
                HeardNothing?.Invoke();
                return;
            }

            // On the critical path on purpose: correcting after the text is already
            // in the document would mean rewriting what the user is looking at.
            // It costs about a third of a second and has its own timeout, so the
            // worst case is the raw transcription arriving slightly later.
            watch.Restart();
            text = await postProcessor.ProcessAsync(text, context);
            var corrected = watch.Elapsed;

            watch.Restart();
            await injector.InjectAsync(text);

            log.LogInformation(
                "{Audio:F1} s of audio → {Chars} characters · transcription {Transcribe:F2} s · correction {Correct:F2} s · injection {Inject:F2} s · total {Total:F2} s",
                audio.Duration.TotalSeconds, text.Length,
                transcribed.TotalSeconds, corrected.TotalSeconds, watch.Elapsed.TotalSeconds, total.Elapsed.TotalSeconds);

            Dictated?.Invoke(text, context);

            // After injection, never before: the user has their text already, and a
            // slow or failed disk write must not add a millisecond to the latency
            // they actually feel.
            _ = SaveAsync(text, context, audio.Duration);
        }
        catch (Exception ex)
        {
            // A failed dictation must never take the app down: the user is in the
            // middle of typing somewhere else and would lose a background process
            // without ever seeing why.
            log.LogError(ex, "The dictation failed");
        }
        finally
        {
            Interlocked.Exchange(ref busy, 0);
            Transition(DictationState.Idle);
        }
    }

    /// <summary>
    /// Dictating and keeping are the same action, so there is no "save" step for
    /// the user — but the save still cannot fail loudly. If the disk is full or the
    /// database is locked, the dictation already worked; losing the history entry
    /// is worth a log line, not an interruption.
    /// </summary>
    private async Task SaveAsync(string text, DictationContext context, TimeSpan duration)
    {
        try
        {
            var note = await notes.AddAsync(text, context, duration);
            Saved?.Invoke(note);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not save the note; the dictation was written anyway");
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

        // Only LlamaPostProcessor actually owns anything native — the GGUF
        // weights and Vulkan context inside its LlamaEngine.
        // NullPostProcessor and every test double have nothing to release,
        // so the cast is soft rather than widening IPostProcessor itself
        // with an IDisposable member every implementation would have to
        // carry. Forwarding here — instead of registering LlamaEngine as
        // its own DI service in Program.cs — matches this class's existing
        // shape above (hotkey/capture) and Program.cs's own composition-root
        // convention: nothing there disposes the ServiceProvider itself at
        // shutdown, so a container-owned singleton would never actually be
        // released either. This is the one call site both the tray's
        // "Salir" and App.RunUninstall already use.
        (postProcessor as IDisposable)?.Dispose();
    }
}

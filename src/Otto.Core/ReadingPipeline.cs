using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Otto.Core;

public enum ReadingState { Idle, Reading, Paused }

/// <summary>
/// The other direction of the product: Otto reading a screen back to somebody.
///
/// <para>
/// Same shape as <see cref="DictationPipeline"/> and for the same reasons — everything
/// platform-specific sits behind a port, so this is exercisable end to end with no
/// speakers, no synthesiser binary and no clipboard.
/// </para>
/// <para>
/// <b>One rule governs every trigger: while a reading is in progress, anything that
/// would start one stops it instead.</b> The hotkey, the notes window's play button,
/// whatever comes later. The alternative — the hotkey stops but the button restarts, say
/// — is two rules for one feature, and the user has to remember which control they
/// pressed to know what will happen next.
/// </para>
/// </summary>
public sealed class ReadingPipeline : IDisposable
{
    private readonly ISpeechSynthesizer synthesizer;
    private readonly IAudioPlayer player;
    private readonly ISelectionReader selection;
    private readonly ISingleShotHotkey hotkey;
    private readonly ILogger<ReadingPipeline> log;

    private CancellationTokenSource? reading;

    /// <summary>
    /// Cancels the fragment being played without ending the reading — how
    /// <see cref="Repeat"/> gets the current sentence to start over.
    ///
    /// <para>
    /// Linked to <see cref="reading"/> rather than independent of it, so a Stop still
    /// reaches the player through this one too. The two are told apart at the catch site
    /// by asking which token actually fired: a fragment cancelled while the reading's own
    /// token is untouched was cancelled by Repeat, and is not an ending.
    /// </para>
    /// </summary>
    private CancellationTokenSource? fragment;

    /// <summary>
    /// Set by <see cref="Repeat"/> and consumed by the fragment loop.
    ///
    /// <para>
    /// A flag rather than an argument because the request arrives from a different thread
    /// entirely — a click on the controls card, or the hotkey message pump — while the loop
    /// is parked inside <c>PlayAsync</c>.
    /// </para>
    /// </summary>
    private volatile bool repeat;

    private ReadingSpeed speed = ReadingSpeed.Normal;

    private int busy;

    public ReadingPipeline(
        ISpeechSynthesizer synthesizer,
        IAudioPlayer player,
        ISelectionReader selection,
        ISingleShotHotkey hotkey,
        ILogger<ReadingPipeline> log)
    {
        this.synthesizer = synthesizer;
        this.player = player;
        this.selection = selection;
        this.hotkey = hotkey;
        this.log = log;
    }

    public ReadingState State { get; private set; } = ReadingState.Idle;

    public event Action<ReadingState>? StateChanged;

    /// <summary>
    /// How fast the reading plays. Takes effect on the sentence already sounding — see
    /// <see cref="IAudioPlayer.Speed"/> for the obligation that makes that true, and
    /// <see cref="ReadingSpeed"/> for why this is not Piper's own speech-rate parameter.
    /// </summary>
    public ReadingSpeed Speed
    {
        get => speed;

        set
        {
            if (speed == value) return;

            speed = value;
            player.Speed = value.Factor;

            SpeedChanged?.Invoke(value);
        }
    }

    /// <summary>Raised when <see cref="Speed"/> moves, so the controls can say what it is now.</summary>
    public event Action<ReadingSpeed>? SpeedChanged;

    /// <summary>
    /// Nothing was selected and the clipboard was empty too.
    ///
    /// <para>
    /// Distinct from a failure, and it exists for the same reason
    /// <see cref="DictationPipeline.HeardNothing"/> does: without it the user presses the
    /// key, nothing happens, and there is no way to tell "Otto is broken" from "there was
    /// nothing there".
    /// </para>
    /// </summary>
    public event Action? NothingToRead;

    /// <summary>
    /// The key was pressed and there is nothing to answer with. Separate from
    /// <see cref="NothingToRead"/> because the two need different answers: one is "select
    /// something first", the other is about the machine, and a single message covering
    /// both would be wrong half the time.
    ///
    /// <para>
    /// Carries <see cref="SpeechAvailability"/> rather than firing bare, because that same
    /// argument applies inside this event too. A missing engine and a missing voice are
    /// not one situation: only one of them has a button in Ajustes that fixes it, and a
    /// handler that cannot tell them apart will send the other user to press it anyway.
    /// </para>
    /// </summary>
    public event Action<SpeechAvailability>? Unavailable;

    /// <summary>
    /// What Otto actually registered with Windows for reading — the same contract as
    /// <see cref="DictationPipeline.RegisteredHotkey"/>, and the only value the UI may
    /// present as "what is bound right now".
    /// </summary>
    public HotkeyBinding? RegisteredHotkey { get; private set; }

    public void Register(HotkeyBinding binding)
    {
        hotkey.Pressed += Toggle;
        hotkey.Register(binding);
        RegisteredHotkey = binding;
    }

    public void Unregister()
    {
        hotkey.Pressed -= Toggle;
        hotkey.Unregister();
        RegisteredHotkey = null;
    }

    /// <summary>Read whatever the user has selected, or stop if already reading.</summary>
    public void Toggle() => Begin(null);

    /// <summary>
    /// Read a specific text — the notes window's play button. Stops a reading in progress
    /// rather than queueing or restarting, per this class's one rule.
    /// </summary>
    public void Read(string text) => Begin(text);

    public void Stop() => reading?.Cancel();

    /// <summary>
    /// Holds the reading where it is.
    ///
    /// <para>
    /// Deliberately not a third way to stop. <see cref="Toggle"/> and <see cref="Read"/>
    /// both still stop a paused reading, because this class's one rule — anything that
    /// would start a reading stops the one in progress — has to keep meaning the same
    /// thing whether or not the user has paused. Pause is the only control that does
    /// something a second press of the hotkey does not.
    /// </para>
    /// </summary>
    public void Pause()
    {
        if (State != ReadingState.Reading) return;

        player.Pause();
        Transition(ReadingState.Paused);
    }

    /// <summary>Carries on from where <see cref="Pause"/> left off.</summary>
    public void Resume()
    {
        if (State != ReadingState.Paused) return;

        player.Resume();
        Transition(ReadingState.Reading);
    }

    /// <summary>What the card's one pause button does, so it never has to ask the state itself.</summary>
    public void TogglePause()
    {
        if (State == ReadingState.Paused) Resume();
        else Pause();
    }

    /// <summary>
    /// Says the current sentence again from its start.
    ///
    /// <para>
    /// The sentence, not the whole reading, and not a jump backwards through the text.
    /// This is the control somebody reaches for when a phrase went past them while they
    /// were looking at something else, and the unit they missed is the one they just
    /// heard — restarting the document would punish that with everything they had already
    /// followed.
    /// </para>
    /// <para>
    /// Free to implement because the audio is still there: fragments are rendered into a
    /// temporary folder that only gets deleted when the reading ends, so repeating is a
    /// second <c>PlayAsync</c> over a file already on disk rather than a second trip
    /// through Piper.
    /// </para>
    /// <para>
    /// Repeating out of a pause plays. The user asked to hear it again, and a repeat that
    /// silently re-armed a paused fragment would look like a dead button.
    /// </para>
    /// </summary>
    public void Repeat()
    {
        if (State == ReadingState.Idle) return;

        repeat = true;

        player.Resume();
        Transition(ReadingState.Reading);

        // Last, so the loop that wakes up on this cancellation already sees the flag.
        fragment?.Cancel();
    }

    private void Begin(string? text)
    {
        // The guard is taken synchronously, before anything awaits, so a second press
        // arriving while the selection is still being fetched reaches Stop rather than
        // starting a second reading over the top of the first.
        if (Interlocked.Exchange(ref busy, 1) == 1)
        {
            Stop();
            return;
        }

        // A reading stopped while paused leaves the player holding, and IAudioPlayer keeps
        // that intention across fragments on purpose. Cleared here rather than in Stop, so
        // that stopping never has to resume a device it is about to tear down — which
        // would be an audible blip on every stop — while the next reading still cannot be
        // born silent.
        player.Resume();

        var cancellation = new CancellationTokenSource();

        // Published before the async work starts, for the same reason: a Stop landing in
        // the microsecond after Begin returns must have something to cancel.
        reading = cancellation;

        Transition(ReadingState.Reading);

        // Not awaited. This is called from the hotkey's message pump, and blocking that
        // thread would freeze every hotkey in the system — the same trade
        // DictationPipeline.OnReleased makes, and for the same reason.
        _ = RunAsync(text, cancellation);
    }

    private async Task RunAsync(string? text, CancellationTokenSource cancellation)
    {
        try
        {
            if (synthesizer.Availability is var availability && availability != SpeechAvailability.Ready)
            {
                log.LogInformation("The reading was asked for but it is not available: {Reason}", availability);
                Unavailable?.Invoke(availability);
                return;
            }

            text ??= await selection.ReadAsync(cancellation.Token);

            if (string.IsNullOrWhiteSpace(text))
            {
                log.LogInformation("There was nothing selected and nothing on the clipboard");
                NothingToRead?.Invoke();
                return;
            }

            await ReadAloudAsync(text, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // The user pressed the key again. Not a failure, and not worth a log line at
            // anything above debug — stopping is a normal way for a reading to end.
            log.LogDebug("The reading was stopped");
        }
        catch (Exception ex)
        {
            // Same trade as DictationPipeline: a background tool that dies on one bad
            // reading leaves the user with nothing running, and they launched Otto to
            // dictate, not to read.
            log.LogError(ex, "The reading failed");
        }
        finally
        {
            reading = null;
            cancellation.Dispose();
            Interlocked.Exchange(ref busy, 0);
            Transition(ReadingState.Idle);
        }
    }

    /// <summary>
    /// Render one fragment ahead of the one playing, and never more than one.
    ///
    /// <para>
    /// This overlap is the entire reason the text is cut up at all. The listener waits
    /// for the first fragment and nothing else: every later one is rendered while its
    /// predecessor is still in the speaker, so as long as the synthesiser runs faster
    /// than speech plays — Piper measured at x4,6 — the gap never reopens.
    /// </para>
    /// <para>
    /// One ahead, not all of them. Rendering the whole document up front would spend a
    /// process launch and a temp file on every fragment of a text the user is about to
    /// stop three sentences in, which is exactly what somebody skimming a page does.
    /// </para>
    /// </summary>
    private async Task ReadAloudAsync(string text, CancellationToken cancellationToken)
    {
        var chunks = Sentences.Split(text);

        if (chunks.Count == 0)
        {
            NothingToRead?.Invoke();
            return;
        }

        var folder = Directory.CreateTempSubdirectory("otto-lectura-");
        var total = Stopwatch.StartNew();

        // Hoisted out of the loop so the finally can settle it. A cancelled reading
        // leaves a render in flight, and tearing the folder down underneath a piper.exe
        // that still holds a file open fails on Windows — and would leave the process
        // orphaned besides.
        Task<SynthesizedSpeech>? next = null;

        try
        {
            next = RenderAsync(chunks[0], folder.FullName, 0, cancellationToken);

            for (var index = 0; index < chunks.Count; index++)
            {
                var current = await next!;

                next = index + 1 < chunks.Count
                    ? RenderAsync(chunks[index + 1], folder.FullName, index + 1, cancellationToken)
                    : null;

                await PlayFragmentAsync(current.Path, cancellationToken);
            }

            log.LogInformation("Read {Chars} characters in {Chunks} fragments in {Seconds:F1} s",
                text.Length, chunks.Count, total.Elapsed.TotalSeconds);
        }
        finally
        {
            await SettleAsync(next);
            Discard(folder);
        }
    }

    /// <summary>
    /// Plays one fragment, and plays it again for as long as <see cref="Repeat"/> keeps
    /// asking.
    ///
    /// <para>
    /// The linked token is what separates the two ways a fragment can end. A repeat
    /// cancels only this scope, so the reading's own token is still untouched and the
    /// exception is swallowed here; a stop cancels the reading's token, the filter does
    /// not match, and the exception carries on up to <see cref="RunAsync"/> where it
    /// belongs. Catching both would turn every stop into a silent advance to the next
    /// sentence.
    /// </para>
    /// </summary>
    private async Task PlayFragmentAsync(string path, CancellationToken reading)
    {
        while (true)
        {
            using var scope = CancellationTokenSource.CreateLinkedTokenSource(reading);

            // Cleared before the play, never after: a Repeat arriving in the moment
            // between the player returning and this loop reading the flag has to be
            // honoured, not swallowed by a reset that ran later.
            repeat = false;
            fragment = scope;

            try
            {
                await player.PlayAsync(path, scope.Token);
            }
            catch (OperationCanceledException) when (!reading.IsCancellationRequested)
            {
                // Repeat cancelled this fragment. Not an ending, and not worth a log line.
            }
            finally
            {
                fragment = null;
            }

            if (!repeat || reading.IsCancellationRequested) return;
        }
    }

    private Task<SynthesizedSpeech> RenderAsync(string chunk, string folder, int index, CancellationToken cancellationToken) =>
        synthesizer.SpeakAsync(chunk, Path.Combine(folder, $"{index:D4}.wav"), cancellationToken);

    /// <summary>
    /// Wait for an abandoned render to finish failing.
    ///
    /// <para>
    /// Its exception is deliberately dropped: it is almost always the cancellation that
    /// just stopped the reading, and the one thing that must not happen is for it to
    /// surface later as an unobserved task exception attributed to nothing in particular.
    /// </para>
    /// </summary>
    private async Task SettleAsync(Task<SynthesizedSpeech>? pending)
    {
        if (pending is null) return;

        try
        {
            await pending;
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "The fragment being rendered ahead was abandoned");
        }
    }

    /// <summary>
    /// The audio never lives on the user's machine. A reading is rendered, played and
    /// deleted; only an explicit "keep this" moves a file anywhere permanent, and that is
    /// a different feature with a different gesture.
    /// </summary>
    private void Discard(DirectoryInfo folder)
    {
        try
        {
            folder.Delete(recursive: true);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not delete the temporary audio in {Folder}", folder.FullName);
        }
    }

    private void Transition(ReadingState next)
    {
        if (State == next) return;

        State = next;
        StateChanged?.Invoke(next);
    }

    public void Dispose()
    {
        Stop();
        hotkey.Pressed -= Toggle;
        hotkey.Dispose();
    }
}

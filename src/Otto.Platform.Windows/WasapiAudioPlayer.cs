using NAudio.CoreAudioApi;
using NAudio.Wave;
using Otto.Core;

namespace Otto.Platform.Windows;

/// <summary>
/// Plays one fragment and waits for it to finish.
///
/// <para>
/// Sequential on purpose. <see cref="ReadingPipeline"/> renders the next fragment while
/// this one plays, and returning before the audio has actually finished would let the two
/// overlap — a reading turning into two voices talking over each other.
/// </para>
/// <para>
/// WASAPI rather than the older WaveOut, matching the capture side: ADR 0001 picked WASAPI
/// for the microphone, and playing back through a different stack would mean the two
/// halves of the product disagree about which audio subsystem Otto uses.
/// </para>
/// <para>
/// <b>The Speech category is not decoration.</b> Windows routes it differently from media:
/// it survives "mute all other sounds" accessibility settings, ducks background audio
/// instead of competing with it, and on a headset follows the communications device rather
/// than the default one. For a feature whose whole point is reading a screen to somebody
/// who cannot see it, being classified as music would be the wrong answer in every one of
/// those cases.
/// </para>
/// <para>
/// <b>Stateful, unlike every other adapter in this project, and the transport controls are
/// why.</b> Pause and speed are commands about audio that is already sounding, so they
/// have to reach a device this class opened inside a <see cref="PlayAsync"/> call that has
/// not returned yet. That is what <see cref="gate"/> guards: the caller's thread setting
/// <see cref="Speed"/> and the reading's thread swapping devices between fragments are
/// genuinely concurrent, and the fields they share are the live device and the stretcher
/// in front of it.
/// </para>
/// </summary>
public sealed class WasapiAudioPlayer : IAudioPlayer
{
    private readonly Lock gate = new();

    /// <summary>Null between fragments, and for the whole time no reading is happening.</summary>
    private IWavePlayer? device;

    /// <summary>The stretcher in front of <see cref="device"/>, so speed can move mid-fragment.</summary>
    private TempoSampleProvider? stretcher;

    /// <summary>
    /// Kept here rather than only on the stretcher because it has to outlive the fragment:
    /// a reading is a sequence of separate <see cref="PlayAsync"/> calls, and a speed the
    /// user chose during sentence three has to still apply to sentence four.
    /// </summary>
    private double speed = 1.0;

    /// <summary>
    /// The <em>wanted</em> pause state, not the device's. It is deliberately not read off
    /// <see cref="IWavePlayer.PlaybackState"/>: between two fragments there is no device to
    /// ask, and that is exactly the moment a pause is most likely to arrive — a user aiming
    /// at the button is far more likely to hit the gap between two sentences than the
    /// middle of one. Holding the intention here is what lets the next fragment start
    /// already paused instead of talking over a pause the user is looking at.
    /// </summary>
    private bool paused;

    public double Speed
    {
        get { lock (gate) return speed; }

        set
        {
            lock (gate)
            {
                speed = value;

                // Applied to the fragment already sounding, which is the whole obligation
                // the port states. A stretcher absorbs the change on its next window.
                if (stretcher is not null) stretcher.Tempo = value;
            }
        }
    }

    public bool IsPaused
    {
        get { lock (gate) return paused; }
    }

    public void Pause()
    {
        lock (gate)
        {
            paused = true;
            device?.Pause();
        }
    }

    public void Resume()
    {
        lock (gate)
        {
            paused = false;
            device?.Play();
        }
    }

    public async Task PlayAsync(string wavPath, CancellationToken cancellationToken = default)
    {
        using var reader = new WaveFileReader(wavPath);

        var tempo = new TempoSampleProvider(reader.ToSampleProvider(), Speed);

        using var built = new WasapiPlayerBuilder()
            .WithSharedMode()
            .WithCategory(AudioStreamCategory.Speech)
            .WithLatency(100)
            .Build();

        var finished = new TaskCompletionSource();

        built.PlaybackStopped += (_, _) => finished.TrySetResult();

        built.Init(tempo);

        // Published before Play, so a Speed set by another thread in the microseconds
        // between the two lands on this fragment rather than on the next one.
        lock (gate)
        {
            device = built;
            stretcher = tempo;

            // Re-reads the field rather than trusting the constructor argument above: a
            // speed change could have arrived while Init was running.
            tempo.Tempo = speed;
        }

        built.Play();

        // Play first and then pause, rather than skipping Play: WASAPI has nothing to
        // hold until the stream is running, so pausing an uninitialised device is a
        // no-op and the fragment would run at full speed past a pause the user set
        // between sentences.
        lock (gate)
        {
            if (paused) built.Pause();
        }

        try
        {
            // Stopping has to stop the sound, not just the waiting. A reading that keeps
            // talking after the user has asked for silence is the single most irritating
            // way this feature can fail, because they have already decided they are done.
            await using (cancellationToken.Register(() => built.Stop()))
                await finished.Task;
        }
        finally
        {
            // Cleared even on the cancellation path, and paused deliberately survives:
            // stopping a fragment to repeat it must not silently resume a reading the
            // user paused. Only Resume clears that.
            lock (gate)
            {
                device = null;
                stretcher = null;
            }
        }

        // Register above only fires while the token is live, so a token cancelled before
        // playback even started would otherwise be reported as a completed fragment and
        // the pipeline would move on to the next one.
        cancellationToken.ThrowIfCancellationRequested();
    }
}

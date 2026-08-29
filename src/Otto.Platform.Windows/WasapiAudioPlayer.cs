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
/// </summary>
public sealed class WasapiAudioPlayer : IAudioPlayer
{
    public async Task PlayAsync(string wavPath, CancellationToken cancellationToken = default)
    {
        using var reader = new WaveFileReader(wavPath);

        using var device = new WasapiPlayerBuilder()
            .WithSharedMode()
            .WithCategory(AudioStreamCategory.Speech)
            .WithLatency(100)
            .Build();

        var finished = new TaskCompletionSource();

        device.PlaybackStopped += (_, _) => finished.TrySetResult();

        device.Init(reader);
        device.Play();

        // Stopping has to stop the sound, not just the waiting. A reading that keeps
        // talking after the user has asked for silence is the single most irritating way
        // this feature can fail, because they have already decided they are done.
        await using (cancellationToken.Register(() => device.Stop()))
            await finished.Task;

        // Register above only fires while the token is live, so a token cancelled before
        // playback even started would otherwise be reported as a completed fragment and
        // the pipeline would move on to the next one.
        cancellationToken.ThrowIfCancellationRequested();
    }
}

using NAudio.Wave;
using SoundTouch;

namespace Otto.Platform.Windows;

/// <summary>
/// Plays audio faster or slower without moving its pitch.
///
/// <para>
/// <b>Time-stretch, not resampling, and the difference is the whole point.</b> Reading
/// back a 22 kHz voice at 44 kHz is twice as fast and an octave higher — a chipmunk, not
/// a faster reader. SoundTouch's WSOLA instead splices overlapping windows of the signal
/// at the points where they correlate best, so the waveform keeps its period (the pitch)
/// while the timeline shrinks. It is the same technique every podcast player uses for its
/// 1.5x button, for the same reason.
/// </para>
/// <para>
/// Piper has a speech-rate parameter of its own and it sounds better than this, because
/// the model decides where the extra time goes rather than a splice hunting for it. It is
/// unusable here anyway: it applies at synthesis, and <c>ReadingPipeline</c> renders one
/// fragment ahead of the one playing, so a change would reach the sentence after next.
/// See <see cref="Otto.Core.ReadingSpeed"/> for that trade written out.
/// </para>
/// <para>
/// <see cref="Tempo"/> is settable while audio is flowing on purpose — it is the only
/// reason this class exists rather than a resampler — and the processor absorbs the change
/// on the next window rather than restarting anything.
/// </para>
/// <para>
/// SoundTouch.Net is a managed rewrite, so nothing native is added by using it. That was a
/// selection criterion, not luck: the alternative wrappers all reach a
/// <c>SoundTouch.dll</c> that would have to be shipped, and this repository's bar for
/// carrying another binary is high.
/// </para>
/// </summary>
public sealed class TempoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly SoundTouchProcessor processor = new();

    /// <summary>
    /// The pull buffer, sized at roughly 100 ms. SoundTouch needs a window of input before
    /// it can hand anything back, so reading from the source in very small pieces means
    /// several round trips before the first sample appears.
    /// </summary>
    private readonly float[] pulled;

    /// <summary>
    /// Set once the source has run out and <see cref="SoundTouchProcessor.Flush"/> has
    /// pushed the processor's own tail out. Without it the drain would ask an exhausted
    /// source for more for ever, and <see cref="Read"/> would never return 0 — which is
    /// how NAudio is told the fragment is over.
    /// </summary>
    private bool drained;

    public TempoSampleProvider(ISampleProvider source, double tempo)
    {
        this.source = source;

        WaveFormat = source.WaveFormat;

        processor.Channels = WaveFormat.Channels;
        processor.SampleRate = WaveFormat.SampleRate;
        processor.Tempo = tempo;

        pulled = new float[WaveFormat.SampleRate * WaveFormat.Channels / 10];
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>
    /// 1.0 is the recorded speed; 2.0 is twice as fast. Safe to move mid-playback, which
    /// is the entire reason this sits between the reader and the device.
    /// </summary>
    public double Tempo
    {
        get => processor.Tempo;
        set => processor.Tempo = value;
    }

    public int Read(Span<float> buffer)
    {
        var channels = WaveFormat.Channels;

        // SoundTouch counts in frames — samples per channel — while NAudio counts in
        // floats. Mixing the two up is silent: on mono they are identical, so it would
        // work here and break the day a stereo voice is added.
        var wanted = buffer.Length / channels;
        var produced = 0;

        while (produced < wanted)
        {
            var free = wanted - produced;

            var received = processor.ReceiveSamples(buffer.Slice(produced * channels, free * channels), free);

            if (received > 0)
            {
                produced += received;
                continue;
            }

            if (drained) break;

            var read = source.Read(pulled);

            if (read > 0)
            {
                processor.PutSamples(pulled.AsSpan(0, read), read / channels);
                continue;
            }

            // The source is finished, but the processor is still holding the last window.
            // Flushing pushes it out; the next pass through the loop collects it and the
            // one after that ends on drained.
            processor.Flush();
            drained = true;
        }

        return produced * channels;
    }
}

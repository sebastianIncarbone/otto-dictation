using NAudio.Wave;
using Otto.Core;

// NAudio also has an AudioBuffer; ours is the one this file means.
using AudioBuffer = Otto.Core.AudioBuffer;

namespace Otto.App;

/// <summary>
/// Replaces the microphone with a WAV file so the pipeline can be exercised end to
/// end — hotkey, transcription, injection — without needing someone to speak into
/// a microphone at the right moment.
///
/// Test scaffolding, wired only behind <c>--selftest</c>.
/// </summary>
public sealed class FileAudioCapture(string path) : IAudioCapture
{
    public void Start() { }

    public AudioBuffer Stop()
    {
        using var reader = new AudioFileReader(path);

        var total = (int)(reader.Length / sizeof(float));
        var samples = new float[total];

        ISampleProvider provider = reader;

        var offset = 0;
        int read;
        while (offset < total && (read = provider.Read(samples.AsSpan(offset, total - offset))) > 0)
            offset += read;

        return new AudioBuffer(samples);
    }

    public void Dispose() { }
}

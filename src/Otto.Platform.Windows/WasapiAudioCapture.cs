using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Otto.Core;

// NAudio also has an AudioBuffer; ours is the one this file means.
using AudioBuffer = Otto.Core.AudioBuffer;

namespace Otto.Platform.Windows;

/// <summary>
/// Captures the microphone through WASAPI and hands back 16 kHz mono float32,
/// which is what whisper.cpp consumes without further conversion.
/// </summary>
public sealed class WasapiAudioCapture : IAudioCapture
{
    private readonly ILogger<WasapiAudioCapture> log;

    private WasapiRecorder? recorder;
    private MemoryStream? raw;
    private WaveFormat? deviceFormat;
    private bool sawAudio;

    public WasapiAudioCapture(ILogger<WasapiAudioCapture> log) => this.log = log;

    public void Start()
    {
        Cleanup();

        using var devices = new MMDeviceEnumerator();
        var device = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

        raw = new MemoryStream();
        sawAudio = false;

        recorder = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .Build();

        deviceFormat = recorder.WaveFormat;

        recorder.DataAvailable += OnData;
        recorder.StartRecording();
    }

    /// <summary>
    /// WASAPI reports a Silent flag per buffer. Every buffer silent for a whole
    /// dictation almost always means Windows is blocking microphone access for
    /// desktop apps — which otherwise fails as "Otto transcribes nothing" with no
    /// hint of why.
    /// </summary>
    private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if ((flags & AudioClientBufferFlags.Silent) == 0) sawAudio = true;

        raw?.Write(buffer);
    }

    public AudioBuffer Stop()
    {
        if (recorder is null || raw is null || deviceFormat is null) return new AudioBuffer([]);

        recorder.StopRecording();
        recorder.DataAvailable -= OnData;

        // Capture stops asynchronously; let the last buffer land.
        Thread.Sleep(120);

        if (!sawAudio)
            log.LogWarning(
                "The microphone returned nothing but silence. Check that 'Let desktop apps access " +
                "your microphone' is enabled in the Windows privacy settings.");

        var samples = Resample(raw, deviceFormat);
        Cleanup();

        return new AudioBuffer(samples);
    }

    private static float[] Resample(MemoryStream raw, WaveFormat sourceFormat)
    {
        raw.Position = 0;
        if (raw.Length == 0) return [];

        using var source = new RawSourceWaveStream(raw, sourceFormat);

        var mono = ToMono(source.ToSampleProvider());
        var resampled = new WdlResamplingSampleProvider(mono, AudioBuffer.SampleRate);

        var collected = new List<float>((int)(raw.Length / 4));
        var chunk = new float[AudioBuffer.SampleRate];

        int read;
        while ((read = resampled.Read(chunk.AsSpan())) > 0)
            collected.AddRange(chunk.AsSpan(0, read));

        return [.. collected];
    }

    private static ISampleProvider ToMono(ISampleProvider source) => source.WaveFormat.Channels switch
    {
        1 => source,
        2 => new StereoToMonoSampleProvider(source) { LeftVolume = 0.5f, RightVolume = 0.5f },
        _ => new MultiplexingSampleProvider([source], 1),
    };

    private void Cleanup()
    {
        recorder?.Dispose();
        recorder = null;

        raw?.Dispose();
        raw = null;
    }

    public void Dispose() => Cleanup();
}

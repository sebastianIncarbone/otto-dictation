using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Otto.Bench;

/// <summary>
/// Captures the test clips through WASAPI and writes them as 16 kHz mono WAV —
/// the format whisper.cpp consumes natively.
///
/// This mirrors what the real app will do (ADR 0001: NAudio / WasapiCapture), so
/// a problem in the capture path shows up here rather than at milestone 1.
/// </summary>
public static class Recorder
{
    private const int TargetSampleRate = 16_000;

    public static void RecordAll(string clipsDir, string? onlyId, bool force)
    {
        Directory.CreateDirectory(clipsDir);

        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        Console.WriteLine($"Micrófono: {device.FriendlyName}");
        Console.WriteLine();

        var clips = onlyId is null
            ? TestClips.Load(clipsDir)
            : [TestClips.ById(onlyId) ?? throw new ArgumentException($"No existe el clip '{onlyId}'")];

        foreach (var clip in clips)
        {
            var path = Path.Combine(clipsDir, clip.FileName);

            Console.WriteLine($"── {clip.Id}  ·  {clip.Category}");
            if (clip.ExpectsSilence)
                Console.WriteLine("   (este clip tiene que quedar en silencio)");
            else
                Console.WriteLine($"   Decí: \"{clip.Reference}\"");

            if (!string.IsNullOrEmpty(clip.Instruction))
                Console.WriteLine($"   Nota: {clip.Instruction}");

            if (File.Exists(path) && !force)
            {
                Console.Write("   Ya existe. ¿Regrabar? [s/N] ");
                if (!string.Equals(Console.ReadLine()?.Trim(), "s", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine();
                    continue;
                }
            }

            Console.Write("   ENTER para grabar…");
            Console.ReadLine();

            var seconds = CaptureUntilEnter(device, path);
            Console.WriteLine($"   Guardado: {clip.FileName}  ({seconds:F1} s)");
            Console.WriteLine();
        }

        Console.WriteLine("Listo. Los clips quedaron en:");
        Console.WriteLine($"  {Path.GetFullPath(clipsDir)}");
    }

    private static double CaptureUntilEnter(MMDevice device, string path)
    {
        using var capture = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .Build();

        using var raw = new MemoryStream();

        // NAudio 3 hands out a zero-copy span over the WASAPI buffer, valid only
        // for the duration of the callback — so copy it out immediately.
        capture.DataAvailable += (buffer, _, _, _) => raw.Write(buffer);
        capture.StartRecording();

        Console.Write("   ● GRABANDO — ENTER para cortar…");
        Console.ReadLine();

        capture.StopRecording();
        // WASAPI stops asynchronously; give the last buffer time to land.
        Thread.Sleep(150);

        return WriteResampled(raw, capture.WaveFormat, path);
    }

    /// <summary>
    /// The capture device hands us its own mix format (typically 48 kHz stereo
    /// float). Downmix to mono and resample to 16 kHz before writing.
    /// </summary>
    private static double WriteResampled(MemoryStream raw, WaveFormat sourceFormat, string path)
    {
        raw.Position = 0;

        using var source = new RawSourceWaveStream(raw, sourceFormat);
        var mono = ToMono(source.ToSampleProvider());
        var resampled = new WdlResamplingSampleProvider(mono, TargetSampleRate);

        WaveFileWriter.CreateWaveFile16(path, resampled);

        return new FileInfo(path).Length / (double)(TargetSampleRate * 2);
    }

    private static ISampleProvider ToMono(ISampleProvider source) => source.WaveFormat.Channels switch
    {
        1 => source,
        2 => new StereoToMonoSampleProvider(source) { LeftVolume = 0.5f, RightVolume = 0.5f },
        _ => new MultiplexingSampleProvider([source], 1),
    };
}

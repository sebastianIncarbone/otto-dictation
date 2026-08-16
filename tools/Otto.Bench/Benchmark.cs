using System.Diagnostics;
using System.Text;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Otto.Bench;

public sealed record ClipResult(
    TestClip Clip,
    double AudioSeconds,
    double InferenceSeconds,
    string Text)
{
    /// <summary>Inference time divided by audio duration. Under 0.1 means a 15 s clip lands in 1,5 s.</summary>
    public double RealtimeFactor => AudioSeconds > 0 ? InferenceSeconds / AudioSeconds : 0;

    public double Wer => Accuracy.WordErrorRate(Clip.Reference, Text);

    public int HallucinatedWords => Clip.ExpectsSilence ? Accuracy.HallucinatedWords(Text) : 0;
}

public sealed record ModelResult(
    ModelSpec Model,
    double LoadSeconds,
    IReadOnlyList<ClipResult> Clips);

public sealed class Benchmark(string clipsDir, string modelsDir, bool useVad) : IDisposable
{
    private WhisperVadFactory? vadFactory;
    private WhisperVadProcessor? vad;

    /// <summary>
    /// Loaded once and reused. The first version rebuilt this per clip, inside the
    /// stopwatch — model load time was being reported as inference time.
    /// </summary>
    private WhisperVadProcessor Vad
    {
        get
        {
            if (vad is not null) return vad;

            vadFactory = WhisperVadFactory.FromPath(Path.Combine(modelsDir, Models.VadFileName));
            return vad = vadFactory.CreateBuilder().Build();
        }
    }

    public void Dispose()
    {
        vad?.Dispose();
        vadFactory?.Dispose();
    }

    public static void SetRuntimeOrder(string runtime)
    {
        // Each option lists only its own libraries: a fallback chain would silently
        // answer a CUDA request with CPU numbers, which is worse than an error.
        RuntimeOptions.RuntimeLibraryOrder = runtime.ToLowerInvariant() switch
        {
            "cuda"   => [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12],
            "vulkan" => [RuntimeLibrary.Vulkan],
            "cpu"    => [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            "auto"   => [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12, RuntimeLibrary.Vulkan,
                         RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            _ => throw new ArgumentException($"Runtime desconocido: '{runtime}'. Usá cuda, vulkan, cpu o auto."),
        };
    }

    public static string ConfigureRuntime(string runtime)
    {
        SetRuntimeOrder(runtime);

        // The native library binds once per process and stays bound, which is why
        // comparing runtimes means running this tool once per runtime rather than
        // looping here. See ADR 0001 §5.4.
        try
        {
            return WhisperFactory.GetRuntimeInfo() ?? "(sin información de runtime)";
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"El runtime '{runtime}' {Probe.Explain(runtime)}{Environment.NewLine}" +
                $"Corré 'otto-bench probe' para ver qué runtimes andan en esta máquina." +
                $"{Environment.NewLine}Detalle: {ex.Message}", ex);
        }
    }

    public async Task<ModelResult> RunAsync(ModelSpec model, string language)
    {
        var modelPath = Path.Combine(modelsDir, model.FileName);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Falta el modelo {model.FileName}. Corré: otto-bench models");

        var loadWatch = Stopwatch.StartNew();
        using var factory = WhisperFactory.FromPath(modelPath);
        loadWatch.Stop();

        using var processor = factory.CreateBuilder()
            .WithLanguage(language)
            .WithNoContext()
            .Build();

        var results = new List<ClipResult>();
        var warmedUp = false;

        foreach (var clip in TestClips.Load(clipsDir))
        {
            var path = Path.Combine(clipsDir, clip.FileName);
            if (!File.Exists(path))
            {
                Console.WriteLine($"    ! falta {clip.FileName} — salteado");
                continue;
            }

            var samples = ReadSamples(path);

            // The first inference pays for lazy native initialisation. Measuring it
            // would make whichever clip runs first look artificially slow.
            if (!warmedUp)
            {
                await TranscribeAsync(processor, samples);
                warmedUp = true;
            }

            var watch = Stopwatch.StartNew();
            var text = await TranscribeAsync(processor, samples);
            watch.Stop();

            results.Add(new ClipResult(
                clip,
                samples.Length / 16_000d,
                watch.Elapsed.TotalSeconds,
                text.Trim()));

            Console.WriteLine($"    {clip.Id,-14} {watch.Elapsed.TotalSeconds,6:F2}s");
        }

        return new ModelResult(model, loadWatch.Elapsed.TotalSeconds, results);
    }

    private async Task<string> TranscribeAsync(WhisperProcessor processor, float[] samples)
    {
        if (!useVad)
            return await CollectAsync(processor, samples);

        // VAD is used as a GATE, not a splitter.
        //
        // The obvious implementation — detect every speech region and transcribe
        // each one — is a trap. whisper.cpp pads every call to a fixed 30-second
        // window, so N regions cost N full inferences: measured at ~10x the
        // latency of a single pass. It also transcribes worse, because splitting
        // throws away the surrounding context Whisper uses to disambiguate.
        //
        // So: ask VAD one question — is there speech at all? No speech means
        // return empty without ever invoking the model, which is what stops the
        // silence hallucination (ADR 0001 §5.7). If there is speech, trim to the
        // span between the first and last region and run ONE inference over it.
        var speeches = Vad.DetectSpeech(samples);
        if (speeches.Count == 0) return string.Empty;

        var start = Math.Max(0, (int)(speeches[0].Start.TotalSeconds * 16_000));
        var end = Math.Min((int)(speeches[^1].End.TotalSeconds * 16_000), samples.Length);
        if (end <= start) return string.Empty;

        return await CollectAsync(processor, samples[start..end]);
    }

    private static async Task<string> CollectAsync(WhisperProcessor processor, float[] samples)
    {
        var builder = new StringBuilder();

        await foreach (var segment in processor.ProcessAsync(samples))
            builder.Append(segment.Text);

        return builder.ToString();
    }

    private static float[] ReadSamples(string path)
    {
        using var reader = new AudioFileReader(path);

        var total = (int)(reader.Length / sizeof(float));
        var samples = new float[total];

        // AudioFileReader inherits WaveStream.Read(byte[], …) and implements
        // ISampleProvider.Read(float[], …). The class method hides the interface
        // one, so the cast is what selects the float overload.
        ISampleProvider provider = reader;

        var offset = 0;
        int read;
        while (offset < total && (read = provider.Read(samples.AsSpan(offset, total - offset))) > 0)
            offset += read;

        return samples;
    }
}

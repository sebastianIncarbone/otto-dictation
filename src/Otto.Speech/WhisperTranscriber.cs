using System.Text;
using Microsoft.Extensions.Logging;
using Otto.Core;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Otto.Speech;

public sealed record TranscriberOptions
{
    public required string ModelPath { get; init; }
    public required string VadModelPath { get; init; }
    public string Language { get; init; } = "es";

    /// <summary>
    /// Preferred native backends, in order. CUDA is deliberately absent: it needs
    /// the CUDA Toolkit installed, which cannot be assumed on a user's machine.
    /// Vulkan only needs the graphics driver.
    /// </summary>
    public IReadOnlyList<RuntimeLibrary> Runtimes { get; init; } =
        [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
}

public sealed class WhisperTranscriber : ITranscriber
{
    private readonly TranscriberOptions options;
    private readonly IPromptProvider prompts;
    private readonly ILogger<WhisperTranscriber> log;

    private WhisperFactory? factory;
    private WhisperVadFactory? vadFactory;
    private WhisperVadProcessor? vad;

    /// <summary>
    /// One processor per prompt. Building one is cheap next to loading the model,
    /// and the prompt is fixed at build time, so contexts are cached rather than
    /// rebuilt on every dictation.
    /// </summary>
    private readonly Dictionary<string, WhisperProcessor> processors = [];
    private readonly SemaphoreSlim gate = new(1, 1);

    public WhisperTranscriber(TranscriberOptions options, IPromptProvider prompts, ILogger<WhisperTranscriber> log)
    {
        this.options = options;
        this.prompts = prompts;
        this.log = log;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        RuntimeOptions.RuntimeLibraryOrder = [.. options.Runtimes];

        factory = WhisperFactory.FromPath(options.ModelPath);

        vadFactory = WhisperVadFactory.FromPath(options.VadModelPath);
        vad = vadFactory.CreateBuilder().Build();

        log.LogInformation("Whisper ready — runtime {Runtime}", WhisperFactory.GetRuntimeInfo());

        await WarmUpAsync(cancellationToken);
    }

    /// <summary>
    /// Runs one throwaway inference so the first real dictation does not pay for it.
    ///
    /// Vulkan compiles its compute pipelines the first time they are used, and that
    /// was measured at over ten seconds on a machine with no driver shader cache yet
    /// — against 0,8 s once warm. Without this the very first dictation after
    /// installing Otto would look broken. Paying it here means it happens while the
    /// UI already says "cargando modelo".
    /// </summary>
    private async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        // Straight to the processor, bypassing the VAD gate: silence would be
        // filtered out and the model would never actually run.
        var processor = ProcessorFor(null);
        var quiet = new float[AudioBuffer.SampleRate / 2];

        await foreach (var _ in processor.ProcessAsync(quiet, cancellationToken)) { }

        log.LogInformation("Warm-up in {Seconds:F1} s", watch.Elapsed.TotalSeconds);
    }

    public async Task<string> TranscribeAsync(
        AudioBuffer audio,
        DictationContext context,
        CancellationToken cancellationToken = default)
    {
        if (factory is null || vad is null) throw new InvalidOperationException("Call LoadAsync first.");
        if (audio.IsEmpty) return string.Empty;

        // The model is a single native resource; concurrent dictations would
        // corrupt its state rather than run in parallel.
        await gate.WaitAsync(cancellationToken);

        try
        {
            var speech = Trim(audio.Samples);
            if (speech.Length == 0)
            {
                log.LogDebug("The VAD found no speech; the model is not invoked");
                return string.Empty;
            }

            var prompt = prompts.PromptFor(context);
            var processor = ProcessorFor(prompt);

            var builder = new StringBuilder();

            await foreach (var segment in processor.ProcessAsync(speech, cancellationToken))
                builder.Append(segment.Text);

            return builder.ToString().Trim();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// VAD as a gate, not a splitter.
    ///
    /// Transcribing each detected region separately costs one full inference per
    /// region — whisper.cpp pads every call to a fixed 30-second window — and
    /// measured about ten times slower. It also transcribes worse, because
    /// splitting discards the context the model uses to disambiguate.
    ///
    /// So: no speech at all means return nothing without invoking the model, which
    /// is what stops it inventing text out of silence. Otherwise trim to the span
    /// from the first region to the last and transcribe it in one pass.
    /// </summary>
    private float[] Trim(float[] samples)
    {
        var regions = vad!.DetectSpeech(samples);
        if (regions.Count == 0) return [];

        var start = Math.Max(0, (int)(regions[0].Start.TotalSeconds * AudioBuffer.SampleRate));
        var end = Math.Min((int)(regions[^1].End.TotalSeconds * AudioBuffer.SampleRate), samples.Length);

        return end > start ? samples[start..end] : [];
    }

    private WhisperProcessor ProcessorFor(string? prompt)
    {
        var key = prompt ?? "";

        if (processors.TryGetValue(key, out var cached)) return cached;

        var builder = factory!.CreateBuilder()
            .WithLanguage(options.Language)
            .WithNoContext();

        // WithNoContext stops carry-over between dictations; the prompt is the one
        // piece of context that is meant to persist, so it is applied after.
        if (!string.IsNullOrWhiteSpace(prompt))
            builder = builder.WithPrompt(prompt);

        return processors[key] = builder.Build();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var processor in processors.Values)
            await processor.DisposeAsync();

        processors.Clear();

        vad?.Dispose();
        vadFactory?.Dispose();
        factory?.Dispose();
        gate.Dispose();
    }
}

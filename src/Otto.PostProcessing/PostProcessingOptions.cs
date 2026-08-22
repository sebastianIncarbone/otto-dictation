namespace Otto.PostProcessing;

/// <summary>Everything <see cref="LlamaPostProcessor"/> needs to correct a dictation.</summary>
public sealed record PostProcessingOptions
{
    /// <summary>
    /// Hard ceiling on the correction. Past this the raw transcription goes in —
    /// a user waiting on their own words will not trade seconds for conjugations.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Separate from <see cref="Timeout"/>. The probe/load runs once at startup,
    /// where a couple of extra seconds cost nothing; the correction runs while
    /// someone waits for their own words. Reusing the hot-path budget here made
    /// Otto declare a perfectly healthy engine unavailable before it finished
    /// loading.
    /// </summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Absolute path to the correction GGUF on disk.</summary>
    public string ModelPath { get; init; } = "";

    /// <summary>
    /// <see cref="VoseoPrompt"/>'s system message plus its few-shot examples costs
    /// ~700 tokens fixed; a 512-token output cap plus a 1,024-token dictation
    /// allowance rounds up to 2,236, with margin. Qwen2.5-3B's GQA KV cache costs
    /// ~36 KB/token, so 4096 (~147 MB) instead of the model's native 32k
    /// (~1.2 GB) is a deliberate tradeoff. <see cref="LlamaPostProcessor"/> returns
    /// the raw text instead of relying on llama.cpp to truncate or throw when a
    /// dictation would not fit inside the 1,024-token allowance.
    /// </summary>
    public int ContextSize { get; init; } = 4096;

    /// <summary>Hard cap on generated tokens per correction.</summary>
    public int MaxTokens { get; init; } = 512;

    /// <summary>
    /// How long the correction model can sit unused before <see cref="LlamaPostProcessor"/>
    /// unloads it to free VRAM. Defaults to 15 minutes — long enough that
    /// consecutive dictations a few minutes apart never pay for a reload
    /// (and re-pay Vulkan's own first-use pipeline compile, see
    /// <c>WhisperTranscriber.WarmUpAsync</c>'s own doc comment for the
    /// measured cost of that), short enough that leaving Otto running
    /// overnight does not hold roughly a gigabyte and a half of VRAM for
    /// nothing. Null means "never" — the model stays resident for the life
    /// of the process, matching the feature's original always-loaded
    /// behaviour, and is the only way Otto.App's
    /// <c>Settings.CorrectionIdleUnloadMinutes</c> == 0 is representable here.
    /// </summary>
    public TimeSpan? IdleUnloadInterval { get; init; } = TimeSpan.FromMinutes(15);
}

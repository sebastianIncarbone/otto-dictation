namespace Otto.Speech;

/// <summary>
/// Where the two model files live, and what to call them while asking. Kept
/// separate from <see cref="TranscriberOptions"/> on purpose: this is what the
/// downloader needs before either file exists, and <see cref="TranscriberOptions"/>
/// is built from <see cref="SpeechPath"/>/<see cref="VadPath"/> so the transcriber
/// and the downloader can never disagree about where the model is.
/// </summary>
public sealed record ProvisioningOptions
{
    public required string ModelsDirectory { get; init; }
    public required string SpeechFileName { get; init; }
    public required string VadFileName { get; init; }

    /// <summary>Human-readable model name, for the download copy ("large-v3-turbo").</summary>
    public required string Label { get; init; }

    /// <summary>Human-readable size, for the download copy ("~1,6 GB").</summary>
    public required string Size { get; init; }

    /// <summary>
    /// Whether this machine has GPU acceleration. Not <c>Otto.Platform.Windows</c>'s
    /// own <c>Acceleration</c> enum — <c>Otto.Speech</c> must not reference that
    /// project — so the composition root (<c>Program.cs</c>) collapses
    /// <c>HardwareProbe.Detect()</c> to this bool before building the options.
    /// Defaults to true so every existing caller that never set it (this record
    /// predates the correction leg) keeps behaving exactly as before.
    /// </summary>
    public bool HasGpu { get; init; } = true;

    /// <summary>
    /// The correction model's coordinates. All four are optional together: a
    /// caller that never sets them (Program.cs, until the GGUF coordinates are
    /// wired in) gets a third leg that is simply not configured, and
    /// <see cref="ModelProvisioner"/> skips it — the same "everything optional
    /// degrades to nothing" shape as a missing Ollama connection today.
    /// </summary>
    public string? CorrectionFileName { get; init; }

    /// <summary>Absolute download address — the GGUF's host is not whisper.cpp's, so
    /// unlike <see cref="SpeechFileName"/> it cannot be composed from a base URL
    /// <c>Otto.Speech</c> already knows.</summary>
    public string? CorrectionUrl { get; init; }

    /// <summary>Human-readable model name, for the download copy.</summary>
    public string? CorrectionLabel { get; init; }

    /// <summary>Human-readable size, for the download copy.</summary>
    public string? CorrectionSize { get; init; }

    public string SpeechPath => Path.Combine(ModelsDirectory, SpeechFileName);
    public string VadPath => Path.Combine(ModelsDirectory, VadFileName);
    public string? CorrectionPath => CorrectionFileName is null ? null : Path.Combine(ModelsDirectory, CorrectionFileName);

    /// <summary>
    /// The correction leg's download coordinates, or four nulls when the leg
    /// should not be offered at all: on CPU-only hardware (a 3B model can
    /// never land inside the 2s dictation budget, so downloading it would
    /// only cost bandwidth — see <see cref="HasGpu"/>'s own doc) or when the
    /// user has explicitly turned the feature off (<c>Settings.CorrectVoseo</c>
    /// == false). Otto.App's <c>Program.cs</c> calls this instead of assigning
    /// the four <c>Correction*</c> properties directly, so the decision is a
    /// plain static method — testable with no DI container, no
    /// <c>Settings</c> type (this project must not reference <c>Otto.App</c>),
    /// and no mocks. <see cref="ModelProvisioner"/> already treats a
    /// <see langword="null"/> <see cref="CorrectionFileName"/>/<see cref="CorrectionUrl"/>
    /// as "nothing to provision" — see its own
    /// <see cref="ModelProvisioner.NeedsProvisioning"/> doc comment — so
    /// returning nulls here is the entire fix; no change to
    /// <see cref="ModelProvisioner"/> itself was needed.
    /// </summary>
    public static (string? FileName, string? Url, string? Label, string? Size) CorrectionCoordinates(
        bool hasGpu,
        bool correctVoseo,
        string fileName,
        string url,
        string label,
        string size) =>
        hasGpu && correctVoseo ? (fileName, url, label, size) : (null, null, null, null);
}

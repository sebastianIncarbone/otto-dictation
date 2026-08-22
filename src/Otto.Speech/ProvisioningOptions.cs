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
    /// The correction leg's download coordinates, or four nulls on CPU-only
    /// hardware, where a 3B model can never land inside the 2s dictation
    /// budget — see <see cref="HasGpu"/>'s own doc comment — so downloading
    /// it would only cost bandwidth for a feature that can never work.
    /// Otto.App's <c>Program.cs</c> calls this instead of assigning the four
    /// <c>Correction*</c> properties directly, so the decision is a plain
    /// static method — testable with no DI container, no <c>Settings</c>
    /// type (this project must not reference <c>Otto.App</c>), and no mocks.
    ///
    /// Gated on <see cref="HasGpu"/> ALONE — deliberately NOT on
    /// <c>Settings.CorrectVoseo</c>, which this method took as a parameter
    /// before correction could be switched on at runtime. <see cref="ProvisioningOptions"/>
    /// is built once, at startup; if these coordinates depended on
    /// CorrectVoseo's value at THAT moment, a GPU user who started with
    /// correction off would have <see cref="CorrectionFileName"/>/
    /// <see cref="CorrectionUrl"/> stuck null for the rest of the process —
    /// the GGUF could never be downloaded even after turning correction back
    /// on, because <see cref="ModelProvisioner.ProvisionAsync"/>'s third leg
    /// has nothing to fetch without them. Hardware support and the user's
    /// current preference are now two separate questions: this answers only
    /// the first one, permanently, for the life of the process. The second
    /// one — should a download actually run RIGHT NOW — belongs to whoever
    /// is asking, at the moment they ask, which is why
    /// <see cref="ModelProvisioner.NeedsProvisioning"/> and
    /// <see cref="ModelProvisioner.ProvisionAsync"/> both take a live
    /// <c>correctionEnabled</c> parameter instead of reading it from here.
    /// </summary>
    public static (string? FileName, string? Url, string? Label, string? Size) CorrectionCoordinates(
        bool hasGpu,
        string fileName,
        string url,
        string label,
        string size) =>
        hasGpu ? (fileName, url, label, size) : (null, null, null, null);
}

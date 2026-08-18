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

    public string SpeechPath => Path.Combine(ModelsDirectory, SpeechFileName);
    public string VadPath => Path.Combine(ModelsDirectory, VadFileName);
}

namespace Otto.PostProcessing;

/// <summary>
/// The correction model, abstracted away from LLamaSharp so <see cref="LlamaPostProcessor"/>
/// can be tested headlessly — no GGUF, no GPU, no LLamaSharp type anywhere outside
/// <see cref="LlamaEngine"/>, which is the only class that implements this against the
/// real native library.
/// </summary>
public interface ICorrectionEngine
{
    /// <summary>
    /// Loads the model into memory. Slow, and expected to run at most once — the
    /// caller (<see cref="LlamaPostProcessor"/>) is what gates concurrent/repeated
    /// calls, not this method.
    /// </summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one chat completion. The last message is the text to correct; everything
    /// before it seeds the model's history (system prompt + worked examples).
    /// </summary>
    Task<string> ChatAsync(
        IReadOnlyList<(string Role, string Content)> messages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Frees the model's native handles while leaving <see cref="LoadAsync"/>
    /// callable again afterward — the idle-unload timer and the runtime
    /// correction on/off toggle both need this, and unlike <see cref="IDisposable.Dispose"/>
    /// (terminal, called once at process shutdown) this can run many times
    /// over the engine's life. Returns false — WITHOUT freeing anything —
    /// when a call already in flight could not be waited out within the
    /// engine's own bounded timeout; the caller must treat that as "still
    /// loaded" rather than lying that the model went away.
    /// </summary>
    Task<bool> UnloadAsync(CancellationToken cancellationToken = default);
}

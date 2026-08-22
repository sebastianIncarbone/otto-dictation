using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using Microsoft.Extensions.Logging;

namespace Otto.PostProcessing;

/// <summary>
/// <see cref="ICorrectionEngine"/> over LLamaSharp — the only class in
/// Otto.PostProcessing that touches LLamaSharp types, so <see cref="LlamaPostProcessor"/>
/// stays testable without a GGUF or a GPU. Modeled on the spike in
/// <c>tools/Otto.Bench/LlamaCorrector.cs</c>, which validated that this coexists in
/// the same process as Whisper's own Vulkan runtime.
/// </summary>
public sealed class LlamaEngine : ICorrectionEngine, IDisposable
{
    private readonly PostProcessingOptions options;
    private readonly ILogger<LlamaEngine> log;

    private LLamaWeights? weights;
    private LLamaContext? context;
    private InteractiveExecutor? executor;

    public LlamaEngine(PostProcessingOptions options, ILogger<LlamaEngine> log)
    {
        this.options = options;
        this.log = log;
    }

    /// <summary>
    /// Forces Vulkan and disables the automatic CPU fallback. Must run after
    /// Whisper's own <c>RuntimeOptions.RuntimeLibraryOrder</c> is set — a real
    /// conflict between the two engines' native libraries has to throw here
    /// instead of silently landing on CPU, where a 3B model blows the whole
    /// two-second budget on every dictation while reporting healthy.
    /// </summary>
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        NativeLibraryConfig.All.WithVulkan(true).WithAutoFallback(false).WithLogCallback(log);

        var parameters = new ModelParams(options.ModelPath)
        {
            ContextSize = (uint)options.ContextSize,
            GpuLayerCount = -1,
        };

        weights = LLamaWeights.LoadFromFile(parameters);
        context = weights.CreateContext(parameters);
        executor = new InteractiveExecutor(context);

        return Task.CompletedTask;
    }

    public async Task<string> ChatAsync(
        IReadOnlyList<(string Role, string Content)> messages,
        CancellationToken cancellationToken = default)
    {
        if (executor is null) throw new InvalidOperationException("Call LoadAsync first.");

        var history = new ChatHistory();
        foreach (var (role, content) in messages.Take(messages.Count - 1))
            history.AddMessage(ToAuthorRole(role), content);

        var (lastRole, lastContent) = messages[^1];
        var session = new ChatSession(executor, history);

        var inferenceParams = new InferenceParams
        {
            MaxTokens = options.MaxTokens,
            AntiPrompts = ["User:", "Usuario:"],
        };

        var builder = new StringBuilder();

        await foreach (var token in session.ChatAsync(
            new ChatHistory.Message(ToAuthorRole(lastRole), lastContent),
            inferenceParams,
            cancellationToken))
        {
            builder.Append(token);
        }

        return builder.ToString().Trim();
    }

    private static AuthorRole ToAuthorRole(string role) => role switch
    {
        "system" => AuthorRole.System,
        "assistant" => AuthorRole.Assistant,
        _ => AuthorRole.User,
    };

    public void Dispose()
    {
        context?.Dispose();
        weights?.Dispose();
    }
}

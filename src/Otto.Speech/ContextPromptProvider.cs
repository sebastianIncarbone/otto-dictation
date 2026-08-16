using Otto.Core;

namespace Otto.Speech;

/// <summary>
/// Picks the `initial_prompt` from the application being dictated into.
///
/// <para>
/// Milestone 0.5 measured that there is no single prompt that wins everywhere: a
/// technical prompt took the proper-noun clip from 67% to 17% error and the mixed
/// technical prose from 33% to 8%, while costing about five points on plain
/// narrative speech. So the prompt is selected per context rather than applied
/// globally — which is why the foreground window is resolved before inference
/// rather than after it.
/// </para>
/// <para>
/// This also removes the need to expose whisper.cpp's ~224-token prompt limit to
/// the user. Injecting only the terms belonging to the current context keeps the
/// prompt small on its own, so the dictionary as a whole can grow freely.
/// </para>
/// </summary>
public sealed class ContextPromptProvider : IPromptProvider
{
    /// <summary>
    /// Reads as speech rather than as a word list, because whisper.cpp conditions
    /// the decoder as if this were the transcript immediately before — it is
    /// priming a continuation, not loading a dictionary.
    /// </summary>
    private const string TechnicalPrompt =
        "Che, fijate: instalá las dependencias con pnpm, corré kubectl y revisá el log de Ollama. " +
        "Después subí el modelo a Hugging Face y decime si el rebase sobre develop rompió el deploy. " +
        "Acordate de mirar el re-render en React y el error de TypeScript.";

    private static readonly HashSet<string> TechnicalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "devenv", "rider64", "idea64", "pycharm64", "webstorm64", "sublime_text",
        "windowsterminal", "wt", "powershell", "pwsh", "cmd", "conemu64", "alacritty", "wezterm",
        "gitkraken", "sourcetree", "docker desktop", "postman", "nvim", "vim",
    };

    private readonly Dictionary<string, string> byProcess;

    public ContextPromptProvider(IReadOnlyDictionary<string, string>? overrides = null) =>
        byProcess = new Dictionary<string, string>(overrides ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);

    public string? PromptFor(DictationContext context)
    {
        if (byProcess.TryGetValue(context.ProcessName, out var custom)) return custom;

        return TechnicalProcesses.Contains(context.ProcessName) ? TechnicalPrompt : null;
    }
}

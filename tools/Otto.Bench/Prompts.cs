namespace Otto.Bench;

/// <summary>
/// Candidate `initial_prompt` values for milestone 0.5.
///
/// whisper.cpp conditions the decoder on this text as if it were the transcript
/// that came immediately before. That is why a prompt works best as natural prose
/// rather than a word list: it is priming a continuation, not loading a dictionary.
///
/// Two failure modes are being attacked at once (see docs/hito-0-resultados.md):
///   - proper nouns the model has never seen written in a Spanish context
///   - Rioplatense voseo being normalised to neutral Spanish
///
/// Hence the variants: each one isolates a factor so the effect is attributable.
///
/// NOTE: whisper.cpp truncates the prompt at roughly 224 tokens. The custom
/// dictionary in the real app has to respect that budget — it cannot grow freely.
/// </summary>
public sealed record PromptVariant(string Key, string Label, string? Text);

public static class Prompts
{
    public static readonly IReadOnlyList<PromptVariant> All =
    [
        new("none", "sin prompt", null),

        // Vocabulary only. Isolates "does injecting the spellings fix the names?"
        new("vocab", "solo vocabulario",
            "Whisper.net, Ollama, Hugging Face, kubectl, pnpm, npm, TypeScript, React, " +
            "Avalonia, WPF, Docker Compose, rebase, branch, merge, deploy, endpoint, re-render."),

        // Register only. Isolates "does anchoring the register bring back the voseo?"
        new("voseo", "solo voseo",
            "Che, mirá esto: fijate si podés y avisame. Corré vos que yo después reviso, " +
            "y contame cómo te fue con eso."),

        // Both, as one natural passage rather than two glued fragments — the prompt
        // is read as preceding speech, so it should sound like speech.
        new("ambos", "vocabulario + voseo",
            "Che, fijate: instalá las dependencias con pnpm, corré kubectl y revisá el log " +
            "de Ollama. Después subí el modelo a Hugging Face y decime si el rebase sobre " +
            "develop rompió el deploy. Acordate de mirar el re-render en React y el error " +
            "de TypeScript."),
    ];

    public static PromptVariant ByKey(string key) =>
        All.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException(
            $"Prompt desconocido: '{key}'. Opciones: {string.Join(", ", All.Select(p => p.Key))}");
}

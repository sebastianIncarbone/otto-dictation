using System.Text.Encodings.Web;
using System.Text.Json;

namespace Otto.Bench;

/// <summary>
/// The fixed set of utterances the benchmark runs against.
///
/// These are recorded ONCE and reused across every model and runtime, so that a
/// comparison measures the model rather than how the speaker happened to talk
/// that day. Reference text is what was actually said, used to compute WER.
/// </summary>
public sealed record TestClip(
    string Id,
    string Category,
    string Reference,
    string Instruction)
{
    public string FileName => $"{Id}.wav";

    /// <summary>Clips that must transcribe to nothing. See ADR 0001 §5.7.</summary>
    public bool ExpectsSilence => Reference.Length == 0;
}

public static class TestClips
{
    public static readonly IReadOnlyList<TestClip> All =
    [
        new("01-control", "Español puro",
            "Che, ¿podés revisar el pull request que subí recién?",
            "Decilo natural, como se lo dirías a un compañero."),

        new("02-anglicismos", "Anglicismos comunes",
            "Hay que hacer el deploy a producción después del merge.",
            "Sin pronunciar en inglés forzado — como hablás normalmente."),

        new("03-comandos", "Comandos de terminal",
            "Corré npm run build y fijate si tira algún error de TypeScript.",
            "Los comandos dichos como los decís en voz alta."),

        new("04-frontend", "Jerga densa",
            "El componente de React no está haciendo el re-render cuando cambia el estado.",
            "Jerga seguida, sin pausas entre términos."),

        new("05-git", "Jerga de git",
            "Necesito hacer un rebase de la branch feature sobre develop.",
            null!),

        new("06-tooling", "Comandos encadenados",
            "Instalá las dependencias con pnpm install y después levantá el docker compose.",
            null!),

        new("07-numeros", "Números y códigos",
            "El endpoint devuelve un cuatrocientos uno, revisá el token de autenticación.",
            "Clave: ver si escribe 401 o \"cuatrocientos uno\". Los dos son defendibles, pero hay que saber cuál hace."),

        new("08-nombres", "Nombres propios difíciles",
            "Whisper punto net, Ollama, Hugging Face y kubectl.",
            "Lista de nombres, con pausas cortas entre cada uno."),

        new("09-proyecto", "Vocabulario del proyecto",
            "Vamos a usar Avalonia en vez de WPF porque necesitamos que corra en Linux.",
            null!),

        new("10-largo", "Frase larga (~15 s)",
            "Estuve pensando en cómo encarar el tema de la latencia y creo que lo mejor " +
            "es dejar el modelo cargado en memoria desde que arranca la aplicación, " +
            "porque si lo cargamos en cada pedido se nos van varios segundos y la " +
            "herramienta deja de ser usable para dictar de forma cotidiana.",
            "Es el test del techo de latencia. Hablá de corrido, sin cortar."),

        // Hallucination cases — ADR 0001 §5.7. Whisper invents text when fed
        // silence; these clips are the regression tests for that.
        new("11-silencio", "Alucinación: silencio total",
            "",
            "Apretá grabar, NO DIGAS NADA durante unos 3 segundos, y cortá."),

        new("12-duda", "Alucinación: duda inicial",
            "Bueno, arranquemos con esto.",
            "Apretá grabar, esperá 2 segundos EN SILENCIO, recién ahí hablá, y cortá."),

        new("13-pausa", "Alucinación: pausa larga",
            "Primero revisamos la latencia y después vemos la precisión.",
            "Decí la primera mitad, callate 2 segundos, y decí la segunda mitad."),
    ];

    public static TestClip? ById(string id) =>
        All.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The built-in reference is a script — what the speaker was asked to say. What
    /// they actually said is usually a little different, and scoring against the
    /// script charges those differences to the model. Anything in this file wins.
    /// </summary>
    public const string OverridesFileName = "referencias.json";

    /// <summary>
    /// This file is written to be edited by hand, so accented characters have to
    /// survive as themselves. The default encoder escapes them to \uXXXX, which
    /// turns a proofreading task back into a decoding one.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IReadOnlyList<TestClip> Load(string clipsDir)
    {
        var path = Path.Combine(clipsDir, OverridesFileName);
        if (!File.Exists(path)) return All;

        var overrides = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                        ?? [];

        return [.. All.Select(clip => overrides.TryGetValue(clip.Id, out var actual) && actual != clip.Reference
            ? clip with { Reference = actual }
            : clip)];
    }

    /// <summary>
    /// Writes the file the speaker edits, seeded with what the model heard rather
    /// than with the script.
    ///
    /// Seeding from the script would mean rewriting each line from memory. Seeding
    /// from the transcription means the text is already mostly right, so the job
    /// shrinks to fixing the handful of words the model got wrong — which is a
    /// proofreading task instead of a recall task.
    /// </summary>
    public static string WriteOverrideTemplate(string clipsDir, IReadOnlyDictionary<string, string> heard)
    {
        Directory.CreateDirectory(clipsDir);
        var path = Path.Combine(clipsDir, OverridesFileName);

        // Never silently discard a previous pass of corrections.
        if (File.Exists(path))
            File.Move(path, path + ".bak", overwrite: true);

        var seeded = All
            .Where(c => !c.ExpectsSilence)
            .ToDictionary(
                c => c.Id,
                c => heard.TryGetValue(c.Id, out var text) && !string.IsNullOrWhiteSpace(text)
                    ? text
                    : c.Reference);

        File.WriteAllText(path, JsonSerializer.Serialize(
            seeded, JsonOptions));

        return path;
    }
}

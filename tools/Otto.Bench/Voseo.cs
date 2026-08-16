namespace Otto.Bench;

/// <summary>
/// The post-processing pass that turns neutral Spanish into Rioplatense.
///
/// This exists because the `initial_prompt` cannot do it: milestone 0.5 measured
/// that Whisper's prior towards neutral Spanish wins even when the exact phrase is
/// in the prompt.
///
/// It cannot be a lookup table either. In Spanish the tú imperative and the third
/// person indicative are the same string — "instala las dependencias" against "el
/// script instala las dependencias" — so a blind replacement corrupts the second
/// one. Telling them apart needs syntax, which is what the model is for.
/// </summary>
public static class Voseo
{
    /// <summary>
    /// Instructions in the system role, written in <b>neutral</b> Spanish.
    ///
    /// The earlier version was written in Rioplatense itself — "Sos", "Cambiá",
    /// "devolvé" — and the model drifted into Portuguese. Final-stressed forms are
    /// exactly what Spanish and Portuguese share, so demonstrating them in the
    /// instructions plausibly dragged the output across the border. Describing the
    /// target in neutral Spanish keeps the instruction language and the output
    /// language separate.
    /// </summary>
    private const string System =
        """
        Eres un corrector de dictado. Tu única tarea es ajustar conjugaciones
        verbales de segunda persona del singular al voseo rioplatense.

        La salida SIEMPRE está en español. Nunca traduzcas. Nunca uses portugués.

        Cambia solamente:
        - Presente de segunda persona: puedes→podés, tienes→tenés, quieres→querés.
        - Imperativo de segunda persona: instala→instalá, corre→corré, mira→mirá.

        No cambies NUNCA la persona de un verbo. Si el sujeto es "yo" o "nosotros",
        el verbo queda exactamente como está.

        No cambies:
        - Verbos en primera persona: "necesito hacer", "revisamos", "vemos".
        - Verbos en tercera persona: "el script instala" queda igual.
        - Verbos en infinitivo: "necesitamos correr" queda igual.
        - El modo del verbo: "cuando cambia" no es "cuando cambie".
        - Ninguna otra palabra. Ni sinónimos, ni orden, ni puntuación, ni términos
          técnicos, ni errores de transcripción.

        Responde con el texto corregido y nada más.
        """;

    /// <summary>
    /// Worked examples, chosen to cover each decision the model has to make: a
    /// change it must apply, two it must resist, and one where the sentence is
    /// already correct. A small model imitates these far more reliably than it
    /// follows the prose above.
    /// </summary>
    private static readonly (string In, string Out)[] Examples =
    [
        ("¿Me puedes revisar el pull request?",
         "¿Me podés revisar el pull request?"),

        ("Instala las dependencias y después levanta el docker compose.",
         "Instalá las dependencias y después levantá el docker compose."),

        ("El script instala las dependencias y el proceso corre en background.",
         "El script instala las dependencias y el proceso corre en background."),

        ("Vamos a usar Avalonia porque necesitamos correr en Linux.",
         "Vamos a usar Avalonia porque necesitamos correr en Linux."),

        // Primera persona, singular y plural. Sin estos ejemplos el modelo asume
        // que todo verbo conjugado es del interlocutor y le aplica voseo igual.
        ("Necesito hacer un rebase de la branch feature.",
         "Necesito hacer un rebase de la branch feature."),

        ("Primero revisamos la latencia y después vemos la precisión.",
         "Primero revisamos la latencia y después vemos la precisión."),
    ];

    public static IReadOnlyList<(string Role, string Content)> Messages(string text)
    {
        var messages = new List<(string, string)> { ("system", System) };

        foreach (var (input, output) in Examples)
        {
            messages.Add(("user", input));
            messages.Add(("assistant", output));
        }

        messages.Add(("user", text));

        return messages;
    }

    /// <summary>Kept for comparison against the message-based version.</summary>
    public static string Prompt(string text) =>
        $"""
        Sos un corrector de dictado. Convertís conjugaciones al español rioplatense.

        EL TEXTO ESTÁ EN ESPAÑOL Y TIENE QUE SEGUIR EN ESPAÑOL.
        Prohibido traducir. Prohibido usar portugués, italiano ni ninguna otra
        lengua. Nada de "fazer", "não", "devolve", "o", "da", "quando".
        Si dudás entre dos palabras, dejá la que ya estaba.

        REGLAS ESTRICTAS:
        - Cambiá SOLO las conjugaciones verbales de segunda persona al voseo.
          Ejemplos: "puedes"→"podés", "tienes"→"tenés", "instala"→"instalá",
          "corre"→"corré", "mira"→"mirá", "revisa"→"revisá", "fíjate"→"fijate".
        - Si el verbo está en TERCERA persona, NO lo toques.
          "el script instala las dependencias" queda igual.
          "el proceso corre en background" queda igual.
        - Si el verbo está en INFINITIVO, NO lo toques.
          "necesitamos correr en Linux" queda igual, NO es "corré".
          "hay que hacer el deploy" queda igual, NO es "hacé".
        - No cambies ninguna otra palabra. No reordenes. No agregues ni saques nada.
          No corrijas el estilo. No mejores la redacción.
        - Si no hay nada que corregir, devolvé el texto exactamente igual.
        - Respondé ÚNICAMENTE con el texto corregido. Sin explicaciones, sin comillas.

        Texto:
        {text}
        """;

    /// <summary>
    /// How far the model strayed, in words.
    ///
    /// Reported next to how many words actually needed changing. A model that
    /// touches twenty words to fix two is not doing the job — it is rewriting the
    /// user's dictation, which is worse than leaving the voseo wrong.
    /// </summary>
    public static int WordsTouched(string before, string after) =>
        EditDistance(Accuracy.Normalize(before, keepAccents: true), Accuracy.Normalize(after, keepAccents: true));

    public static int WordsNeeded(string transcription, string reference) =>
        EditDistance(
            Accuracy.Normalize(transcription, keepAccents: true),
            Accuracy.Normalize(reference, keepAccents: true));

    /// <summary>
    /// The guard rail Otto uses in production: discard the correction when the
    /// model changed more than a conjugation fix could plausibly require, and
    /// insert the raw transcription instead.
    ///
    /// It only sees the input and the output — in production there is no reference
    /// to compare against, because nobody knows what the user actually said. So it
    /// judges proportion: fixing voseo touches a word here and there, while a model
    /// that has started translating, explaining itself or padding will move a large
    /// fraction of the sentence.
    ///
    /// Being wrong about voseo is a nuisance. Being wrong about what the user said
    /// is a broken tool, so this errs towards rejecting.
    /// </summary>
    public static bool IsSafe(string before, string after)
    {
        if (string.IsNullOrWhiteSpace(after)) return false;

        var original = Accuracy.Normalize(before, keepAccents: true);
        var result = Accuracy.Normalize(after, keepAccents: true);

        if (original.Length == 0) return false;

        // Length drift catches the models that answer the instruction instead of
        // following it — commentary and repetition make the output balloon.
        var ratio = result.Length / (double)original.Length;
        if (ratio is < 0.75 or > 1.25) return false;

        // Proportion of the sentence rewritten. Two words of slack so short
        // dictations are not judged too harshly.
        var budget = Math.Max(2, (int)Math.Ceiling(original.Length * 0.20));

        return WordsTouched(before, after) <= budget;
    }

    private static int EditDistance(string[] a, string[] b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}

namespace Otto.PostProcessing;

/// <summary>
/// The instructions that turn neutral Spanish into Rioplatense.
///
/// Every detail here was arrived at by measurement, not taste. The version that
/// looked most natural — rules written in prose, in Rioplatense, in the user
/// message — made the output <b>worse</b> than doing nothing (31% word error
/// against 18% untouched). The version below scores 13%.
///
/// Three things account for the difference:
///
/// <list type="number">
/// <item><b>The instructions are in neutral Spanish, not Rioplatense.</b> Written
/// in voseo, they demonstrate final-stressed forms — precisely what Spanish shares
/// with Portuguese — and the model drifted across the border, returning things like
/// "hay que fazer o deploy depois do margem". Describing the target instead of
/// speaking it keeps the two languages apart.</item>
/// <item><b>They live in the system role.</b> The same text in the user message is
/// followed noticeably less.</item>
/// <item><b>Examples beat rules.</b> A 3B model imitates a demonstrated pattern far
/// more reliably than it follows a description of one.</item>
/// </list>
/// </summary>
public static class VoseoPrompt
{
    public const string System =
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
    /// One example per decision the model has to make: a change it must apply, and
    /// four it must resist. The first-person pair is not optional — without it the
    /// model treats every conjugated verb as the listener's and applies voseo to
    /// "necesito" and "revisamos" too, which costs four points of word error.
    /// </summary>
    public static readonly (string Input, string Output)[] Examples =
    [
        ("¿Me puedes revisar el pull request?",
         "¿Me podés revisar el pull request?"),

        ("Instala las dependencias y después levanta el docker compose.",
         "Instalá las dependencias y después levantá el docker compose."),

        ("El script instala las dependencias y el proceso corre en background.",
         "El script instala las dependencias y el proceso corre en background."),

        ("Vamos a usar Avalonia porque necesitamos correr en Linux.",
         "Vamos a usar Avalonia porque necesitamos correr en Linux."),

        ("Necesito hacer un rebase de la branch feature.",
         "Necesito hacer un rebase de la branch feature."),

        ("Primero revisamos la latencia y después vemos la precisión.",
         "Primero revisamos la latencia y después vemos la precisión."),
    ];
}

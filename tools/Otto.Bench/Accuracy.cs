using System.Globalization;
using System.Text;

namespace Otto.Bench;

/// <summary>
/// Word Error Rate against the reference transcript.
///
/// Deliberately crude: it lowercases, strips punctuation and accents, and does a
/// word-level edit distance. That is enough to rank models against each other,
/// which is all milestone 0 needs. It is NOT a substitute for reading the
/// transcripts — the report prints both.
/// </summary>
public static class Accuracy
{
    public static double WordErrorRate(string reference, string hypothesis) =>
        Rate(reference, hypothesis, keepAccents: false);

    /// <summary>
    /// Accent-sensitive WER.
    ///
    /// The lenient metric strips accents so that harmless spelling noise does not
    /// dominate. But Rioplatense voseo is marked precisely by the accent — "Corré"
    /// against "Corre", "fijate" against "fíjate", "instalá" against "instala" —
    /// so the lenient metric is structurally blind to the exact failure this
    /// project cares about. Report both: the lenient one ranks models, this one
    /// sees the register.
    /// </summary>
    public static double WordErrorRateStrict(string reference, string hypothesis) =>
        Rate(reference, hypothesis, keepAccents: true);

    private static double Rate(string reference, string hypothesis, bool keepAccents)
    {
        var refWords = Normalize(reference, keepAccents);
        var hypWords = Normalize(hypothesis, keepAccents);

        if (refWords.Length == 0)
            return hypWords.Length == 0 ? 0d : 1d;

        return EditDistance(refWords, hypWords) / (double)refWords.Length;
    }

    /// <summary>Words invented out of silence. Only meaningful for the silence clips.</summary>
    public static int HallucinatedWords(string hypothesis) => Normalize(hypothesis).Length;

    public static string[] Normalize(string text, bool keepAccents = false)
    {
        var stripped = new StringBuilder(text.Length);

        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                if (keepAccents) stripped.Append(ch);
                continue;
            }

            if (char.IsLetterOrDigit(ch)) stripped.Append(char.ToLowerInvariant(ch));
            else stripped.Append(' ');
        }

        return stripped.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

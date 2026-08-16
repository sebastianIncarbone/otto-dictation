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
    public static double WordErrorRate(string reference, string hypothesis)
    {
        var refWords = Normalize(reference);
        var hypWords = Normalize(hypothesis);

        if (refWords.Length == 0)
            return hypWords.Length == 0 ? 0d : 1d;

        return EditDistance(refWords, hypWords) / (double)refWords.Length;
    }

    /// <summary>Words invented out of silence. Only meaningful for the silence clips.</summary>
    public static int HallucinatedWords(string hypothesis) => Normalize(hypothesis).Length;

    public static string[] Normalize(string text)
    {
        var stripped = new StringBuilder(text.Length);

        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark) continue;

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

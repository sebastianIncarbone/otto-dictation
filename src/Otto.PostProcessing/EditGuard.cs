using System.Globalization;
using System.Text;

namespace Otto.PostProcessing;

/// <summary>
/// Decides whether a correction is safe to use.
///
/// The failure that matters is not "it missed a verb" — it is "it rewrote a
/// sentence the user actually said". A dictation tool that silently edits meaning
/// is worse than one that leaves the voseo wrong, so this errs towards rejecting.
///
/// It only ever sees the input and the output. In production there is no reference
/// to compare against — nobody knows what the user really said — so the judgement
/// has to be about proportion: fixing conjugations moves a word here and there,
/// while a model that has started translating, explaining itself or padding will
/// move a large fraction of the sentence.
/// </summary>
public static class EditGuard
{
    private const double MaxLengthDrift = 0.25;
    private const double MaxTouchedFraction = 0.20;
    private const int MinimumAllowance = 2;

    public static bool IsSafe(string before, string after)
    {
        if (string.IsNullOrWhiteSpace(after)) return false;

        var original = Words(before);
        var result = Words(after);

        if (original.Length == 0) return false;

        // Length drift catches the models that answer the instruction instead of
        // following it: commentary and repetition make the output balloon.
        var ratio = result.Length / (double)original.Length;
        if (ratio < 1 - MaxLengthDrift || ratio > 1 + MaxLengthDrift) return false;

        var budget = Math.Max(MinimumAllowance, (int)Math.Ceiling(original.Length * MaxTouchedFraction));

        return Distance(original, result) <= budget;
    }

    public static int WordsTouched(string before, string after) => Distance(Words(before), Words(after));

    /// <summary>
    /// Accents are kept, because voseo is marked by exactly them: "corre" against
    /// "corré" has to count as a change or the guard would be blind to the thing
    /// it is guarding.
    /// </summary>
    private static string[] Words(string text)
    {
        var stripped = new StringBuilder(text.Length);

        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                stripped.Append(ch);
                continue;
            }

            stripped.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        return stripped.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static int Distance(string[] a, string[] b)
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

using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Otto.App.Views;

/// <summary>
/// Paints the search terms inside a note's text, so a result explains itself.
///
/// <para>
/// A <see cref="TextBlock"/> cannot have two colours through its
/// <see cref="TextBlock.Text"/> property, so the text arrives here instead and
/// leaves as runs. Attached rather than a converter because it has two inputs —
/// the text and the query — and both can change without the other.
/// </para>
/// </summary>
public static class Highlight
{
    /// <summary>One run of the text, and whether the search asked for it.</summary>
    public readonly record struct Segment(string Text, bool Match);

    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Text", typeof(Highlight));

    public static readonly AttachedProperty<string?> QueryProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Query", typeof(Highlight));

    public static readonly AttachedProperty<IBrush?> BrushProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IBrush?>("Brush", typeof(Highlight));

    static Highlight()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>((block, _) => Paint(block));
        QueryProperty.Changed.AddClassHandler<TextBlock>((block, _) => Paint(block));
        BrushProperty.Changed.AddClassHandler<TextBlock>((block, _) => Paint(block));
    }

    public static string? GetText(TextBlock block) => block.GetValue(TextProperty);
    public static void SetText(TextBlock block, string? value) => block.SetValue(TextProperty, value);

    public static string? GetQuery(TextBlock block) => block.GetValue(QueryProperty);
    public static void SetQuery(TextBlock block, string? value) => block.SetValue(QueryProperty, value);

    public static IBrush? GetBrush(TextBlock block) => block.GetValue(BrushProperty);
    public static void SetBrush(TextBlock block, IBrush? value) => block.SetValue(BrushProperty, value);

    private static void Paint(TextBlock block)
    {
        var brush = GetBrush(block);
        var inlines = new InlineCollection();

        foreach (var segment in Split(GetText(block), GetQuery(block)))
        {
            var run = new Run(segment.Text);

            if (segment.Match && brush is not null) run.Background = brush;

            inlines.Add(run);
        }

        block.Inlines = inlines;
    }

    /// <summary>
    /// Cuts <paramref name="text"/> into the parts the search matched and the
    /// parts it did not.
    ///
    /// <para>
    /// Matches at the start of a word and not anywhere inside one, because that is
    /// what the search itself did: <c>SqliteNoteRepository.ToMatchExpression</c>
    /// hands FTS5 a quoted term with a <c>*</c>, which is prefix matching per token.
    /// Highlighting plain substrings instead would light up half a note the moment
    /// somebody typed a single letter, and would be claiming a match the query
    /// never made.
    /// </para>
    /// <para>
    /// A match covers the whole word, not just the letters typed. The token is what
    /// FTS5 matched; showing four highlighted letters of "downloader" would report
    /// the query back rather than the result.
    /// </para>
    /// <para>
    /// Case and accents are both ignored, for the same reason: FTS5's tokeniser
    /// folds them, so "correccion" is a query that really does return "corrección"
    /// and the highlight has to be able to say why.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Segment> Split(string? text, string? query)
    {
        var body = text ?? string.Empty;

        var terms = (query ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (terms.Length == 0 || body.Length == 0) return [new Segment(body, false)];

        var hits = Hits(body, terms);

        if (hits.Count == 0) return [new Segment(body, false)];

        var segments = new List<Segment>();
        var at = 0;

        foreach (var (start, length) in hits)
        {
            if (start > at) segments.Add(new Segment(body[at..start], false));

            segments.Add(new Segment(body.Substring(start, length), true));
            at = start + length;
        }

        if (at < body.Length) segments.Add(new Segment(body[at..], false));

        return segments;
    }

    private static List<(int Start, int Length)> Hits(string body, string[] terms)
    {
        // Invariant rather than the current culture: this compares a stored note
        // against what someone typed, and the answer must not depend on which
        // machine the app happens to be running on.
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        var hits = new List<(int Start, int Length)>();
        var taken = 0;

        for (var i = 0; i < body.Length; i++)
        {
            // Already inside a match, or in the middle of a word rather than at the
            // start of one.
            if (i < taken) continue;
            if (i > 0 && char.IsLetterOrDigit(body[i - 1])) continue;

            var longest = 0;

            foreach (var term in terms)
            {
                // The matched length is asked for rather than assumed: with accents
                // folded, the run in the note can be a different length from the term
                // that found it.
                var matched = compare.IsPrefix(
                    body.AsSpan(i), term,
                    CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace,
                    out var length);

                if (matched && length > longest) longest = length;
            }

            if (longest == 0) continue;

            // Extended to the end of the word. FTS5 did not match a prefix, it
            // matched the whole token that starts with one — so "down" earned this
            // note through "downloader", and painting four letters of it would be
            // showing what was typed rather than what was found. The loop only ever
            // grows the run, so a term carrying its own punctuation keeps it.
            var end = i + longest;

            while (end < body.Length && char.IsLetterOrDigit(body[end])) end++;

            hits.Add((i, end - i));
            taken = end;
        }

        return hits;
    }
}

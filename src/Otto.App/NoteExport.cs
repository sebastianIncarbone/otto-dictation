using System.Collections.Generic;
using System.Text;
using Otto.App.ViewModels;

namespace Otto.App;

/// <summary>
/// Turns a set of notes into a file someone can keep.
///
/// <para>
/// Pure, and separate from the saving. Choosing where a file goes belongs to a
/// window; deciding what is in it does not, and this half is the half worth
/// testing — a dictation that comes back out mangled is the one failure an export
/// cannot be forgiven.
/// </para>
/// </summary>
public static class NoteExport
{
    /// <summary>
    /// CRLF rather than the platform default, stated once. Otto is Windows-only and
    /// these files are opened by Windows tools; leaving it to <c>AppendLine</c> would
    /// make the output depend on where the code happened to run, which is exactly
    /// the kind of thing a test would then have to guess about.
    /// </summary>
    private const string Break = "\r\n";

    /// <summary>
    /// UTF-8 <em>with</em> the byte-order mark, unusually.
    ///
    /// Otto's whole reason to exist is Rioplatense Spanish, so every export is full
    /// of accents and ñ. A Windows editor that guesses the encoding wrong turns
    /// "corrección" into mojibake, and the mark is what stops it guessing.
    /// </summary>
    public static Encoding Encoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>
    /// Plain text: the note, and above it the two lines that say which note it is.
    /// No rules or markers between entries — a .txt that invents its own syntax is
    /// a worse .txt and not a better .md.
    /// </summary>
    public static string ToPlainText(IReadOnlyList<NoteViewModel> notes)
    {
        var text = new StringBuilder();

        foreach (var note in notes)
        {
            if (text.Length > 0) text.Append(Break);

            text.Append(note.Heading).Append(Break);
            text.Append(note.Subtitle).Append(Break);
            text.Append(Break);
            text.Append(note.Text).Append(Break);
        }

        return text.ToString();
    }

    /// <summary>
    /// Markdown: each note a section, its details in italics under the heading.
    ///
    /// <para>
    /// The note's own text goes in verbatim. Escaping it would be defending against
    /// a dictation that happens to start with a hash or a star, which is rare, and
    /// the cost of defending is a file full of backslashes in front of ordinary
    /// punctuation — which is not rare at all.
    /// </para>
    /// </summary>
    public static string ToMarkdown(IReadOnlyList<NoteViewModel> notes)
    {
        var text = new StringBuilder();

        foreach (var note in notes)
        {
            if (text.Length > 0) text.Append(Break);

            text.Append("## ").Append(note.Heading).Append(Break);
            text.Append(Break);
            text.Append('_').Append(note.Subtitle).Append('_').Append(Break);
            text.Append(Break);
            text.Append(note.Text).Append(Break);
        }

        return text.ToString();
    }

    /// <summary>
    /// What the save dialog offers as a name. Dated rather than numbered, because
    /// the second export lands in the same folder as the first and "notas (2)" says
    /// nothing about which is which.
    /// </summary>
    public static string SuggestedName(DateTimeOffset when, bool markdown) =>
        $"notas-otto-{when:yyyy-MM-dd}.{(markdown ? "md" : "txt")}";
}

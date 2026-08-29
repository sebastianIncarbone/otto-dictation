using System.Text;

namespace Otto.Tts;

/// <summary>
/// Cuts a text into chunks that are synthesised and played one after another.
///
/// <para>
/// This exists because of a hard limitation shared by every engine measured for this
/// feature: <c>piper.exe</c> takes one line in, writes a complete WAV, and exits. There
/// is no streaming, so time-to-first-sound for a whole document would equal the time to
/// generate the whole document. For the use case driving this — someone who cannot see
/// the screen selects a paragraph and expects to hear it — that is the difference
/// between a tool and a joke.
/// </para>
/// <para>
/// Chunking buys back the illusion: generate one fragment, start playing it, and
/// generate the next while the first is still in the speaker. The listener hears sound
/// after the first fragment rather than after the last, and as long as the engine
/// generates faster than speech plays — Piper measured at x4,6 — the gap never reopens.
/// </para>
/// <para>
/// The split is deliberately naive: sentence terminators, with a minimum length so "Sí."
/// does not become its own process launch, and a maximum so a comma-spliced paragraph
/// with no full stop still gets broken up instead of blocking the whole reading. A
/// proper segmenter would be a dependency bought to improve something nobody has yet
/// measured as a problem.
/// </para>
/// </summary>
public static class Sentences
{
    private const int MinimumChunk = 40;
    private const int MaximumChunk = 300;

    /// <summary>
    /// The first fragment is cut short on purpose — but the numbers that chose this
    /// constant came from an engine that did not ship, and that is worth knowing before
    /// anyone tunes it.
    ///
    /// <para>
    /// On Qwen3-TTS, generation cost a near-constant ~1,28 seconds of compute per second
    /// of audio, so a seven-second opening sentence bought nine seconds of silence and
    /// cutting it to a two-second clause was the cheapest latency win available. Piper is
    /// a different shape: at x4,6 the same opening sentence costs about a second and a
    /// half, and the dominant term is no longer the audio length but the per-process
    /// launch — the spike measured Piper climbing from x3,09 to x4,6 on chunk size alone.
    /// </para>
    /// <para>
    /// Which means a very small first fragment now pays a whole process launch for very
    /// little audio, and the optimum has almost certainly moved. The constant is
    /// inherited rather than re-derived, and it is still on the safe side — first sound
    /// arrives early, at some cost in throughput nobody can hear. Re-measure it when
    /// playback exists and the wait is observable; do not assume it is tuned.
    /// </para>
    /// </summary>
    private const int FirstChunk = 45;

    /// <summary>
    /// Lower than <see cref="MinimumChunk"/> so a short opening clause is allowed to be
    /// its own fragment rather than being glued to the next one and inflating exactly the
    /// wait this is here to shorten.
    /// </summary>
    private const int FirstChunkMinimum = 25;

    /// <summary>
    /// A comma is accepted as a break in the opening fragment, and nowhere after it.
    /// Splitting mid-sentence costs a little prosody at the very start; it buys the
    /// listener the knowledge that the machine heard them at all, which at that exact
    /// moment is worth more.
    /// </summary>
    public static IReadOnlyList<string> Split(string text)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();

        // Piper treats newlines as utterance boundaries, and with -f only the last
        // utterance survives into the file. A pasted paragraph would therefore lose
        // everything but its final line — silently, with a valid WAV to show for it.
        // Flattening here rather than in the adapter keeps that fact in one place.
        foreach (var character in text.ReplaceLineEndings(" "))
        {
            current.Append(character);

            var opening = chunks.Count == 0;
            var terminator = character is '.' or '!' or '?' or ';' or '…' || (opening && character is ',' or ':');
            var longEnough = current.Length >= (opening ? FirstChunkMinimum : MinimumChunk);
            var tooLong = current.Length >= (opening ? FirstChunk : MaximumChunk);

            if ((terminator && longEnough) || tooLong)
                Flush(chunks, current);
        }

        Flush(chunks, current);

        return chunks;
    }

    private static void Flush(List<string> chunks, StringBuilder current)
    {
        var chunk = current.ToString().Trim();
        current.Clear();

        // A chunk of pure punctuation produces a WAV of pure silence and costs a full
        // process launch to find that out.
        if (chunk.Any(char.IsLetterOrDigit))
            chunks.Add(chunk);
    }
}

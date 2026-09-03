namespace Otto.Tts;

/// <summary>
/// The three knobs that decide how a Piper reading actually sounds — and what is left
/// of the "effort level" this feature was originally sketched with.
///
/// <para>
/// The original idea was a ladder of models: a cheap one for quick readings, an
/// expensive one when quality matters. Two measurements killed it. The expensive engine
/// (Qwen3-TTS) runs at x0,69 — slower than speech itself, so it falls further behind the
/// longer it reads, which makes it a broken tier rather than a premium one. And Piper's
/// own x_low/low/medium/high ladder cannot be climbed down here, because the only
/// Argentine voice in the catalogue exists at <c>high</c> and nowhere else: a cheaper
/// tier costs the accent, and this product does not get to make that trade.
/// </para>
/// <para>
/// So effort became this instead — how one model is sampled, at no cost in speed or
/// accent. Piper is a VITS model, and VITS predicts phoneme durations stochastically,
/// which is why the same sentence synthesised twice produces different bytes from sample
/// zero. <see cref="NoiseW"/> is how far that duration wanders, and at its 0,8 default it
/// is the most likely source of a stressed syllable landing oddly — an accented vowel
/// given a randomly long or short slot is exactly what "it pronounces accents weirdly"
/// sounds like.
/// </para>
/// <para>
/// That was worth chasing because it was the second suspect, not the first. The first was
/// text encoding, and it is <b>not</b> the cause: feeding espeak-ng deliberately mangled
/// cp1252 bytes and dumping the phonemes with <c>--debug</c> produces the same output,
/// character for character, as clean UTF-8. espeak-ng recovers. The accents live in the
/// acoustic model, which is what these knobs reach.
/// </para>
/// <para>
/// Turning the noise down trades the liveliness that makes a voice sound human for
/// consistency. There is no correct setting, only a preference — which is precisely why
/// this is a setting and not a constant.
/// </para>
/// </summary>
public sealed record PiperVoicing(string Id, string Label, double NoiseW, double NoiseScale, double LengthScale)
{
    /// <summary>Piper's own defaults. Liveliest, and the most variable.</summary>
    public static PiperVoicing Natural { get; } = new("natural", "Natural", 0.8, 0.667, 1.0);

    public static PiperVoicing Balanced { get; } = new("intermedio", "Intermedio", 0.4, 0.6, 1.0);

    /// <summary>Less duration wander: steadier stressed syllables, a flatter voice.</summary>
    public static PiperVoicing Steady { get; } = new("estable", "Estable", 0.2, 0.5, 1.0);

    /// <summary>
    /// Steady, and 8% slower on top. The only preset that touches
    /// <see cref="LengthScale"/>, which is speech rate rather than variability — it is
    /// here because "hard to follow" and "unstable" are different complaints with
    /// different fixes, and somebody using this to have a screen read to them may want
    /// both at once.
    /// </summary>
    public static PiperVoicing Measured { get; } = new("pausado", "Pausado", 0.3, 0.55, 1.08);

    public static IReadOnlyList<PiperVoicing> All { get; } = [Natural, Balanced, Steady, Measured];

    /// <summary>
    /// Resolves a stored setting, falling back to <see cref="Natural"/> rather than
    /// throwing — same contract as <see cref="Voices.Resolve"/>, and for the same reason:
    /// this value comes out of <c>config.json</c>, and an unrecognised one must not stop
    /// Otto from starting.
    /// </summary>
    public static PiperVoicing Resolve(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? Natural
            : All.FirstOrDefault(voicing => voicing.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? Natural;

    /// <summary>
    /// The command-line form. Invariant culture is not decoration: on this machine the
    /// current culture writes <c>0,8</c>, Piper parses it as <c>0</c>, and the reading
    /// comes out with every stochastic knob pinned to zero — a quieter, flatter voice
    /// that nothing reports as an error.
    /// </summary>
    public IEnumerable<string> Arguments()
    {
        yield return "--noise_w";
        yield return NoiseW.ToString(System.Globalization.CultureInfo.InvariantCulture);
        yield return "--noise_scale";
        yield return NoiseScale.ToString(System.Globalization.CultureInfo.InvariantCulture);
        yield return "--length_scale";
        yield return LengthScale.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

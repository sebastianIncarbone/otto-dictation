namespace Otto.Core;

/// <summary>
/// How fast a reading plays, and why this is not the same knob as <c>PiperVoicing</c>.
///
/// <para>
/// Piper already has a speech-rate parameter — <c>LengthScale</c>, the VITS duration
/// predictor — and it is the better-sounding one, because the model itself decides where
/// the extra time goes. It is not what this is, and it cannot be: it applies at synthesis
/// time, and <see cref="ReadingPipeline"/> renders one fragment ahead of the one playing.
/// A speed control built on it would leave the sentence you are listening to untouched
/// and change the one after next, which reads as a button that does nothing.
/// </para>
/// <para>
/// So speed lives in the player instead, as a time-stretch over already-rendered audio —
/// the same thing every podcast application does, and the reason it is a time-stretch and
/// not a resample is pitch: playing a 22 kHz voice back at twice the rate is a chipmunk,
/// not a faster reader.
/// </para>
/// <para>
/// The two knobs stay independent because of that split. <c>PiperVoicing</c> is how the
/// voice sounds; this is how it is played back. Choosing "Pausado" and then pressing x2
/// is coherent — a steadier voice, played faster — rather than two settings fighting over
/// one number.
/// </para>
/// <para>
/// Three steps, not a slider. This is a control the user reaches for mid-sentence, with
/// their attention on what is being read rather than on the widget; a slider asks them to
/// aim, and aiming is exactly what somebody having a screen read to them cannot spare.
/// </para>
/// </summary>
public sealed record ReadingSpeed(string Id, string Label, double Factor)
{
    public static ReadingSpeed Normal { get; } = new("x1", "x1", 1.0);

    public static ReadingSpeed Fast { get; } = new("x1.5", "x1,5", 1.5);

    public static ReadingSpeed Faster { get; } = new("x2", "x2", 2.0);

    /// <summary>In the order the button cycles through them.</summary>
    public static IReadOnlyList<ReadingSpeed> All { get; } = [Normal, Fast, Faster];

    /// <summary>
    /// Resolves a stored setting, falling back to <see cref="Normal"/> rather than
    /// throwing — same contract as <c>Voices.Resolve</c> and <c>PiperVoicing.Resolve</c>,
    /// and for the same reason: this value comes out of <c>config.json</c>, which the user
    /// can edit and an older Otto has already written, and an unrecognised one must not
    /// stop Otto from starting.
    /// </summary>
    public static ReadingSpeed Resolve(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? Normal
            : All.FirstOrDefault(speed => speed.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? Normal;

    /// <summary>
    /// The next step, wrapping back to <see cref="Normal"/> after the last one.
    ///
    /// <para>
    /// One button that cycles rather than three that select. The card this lives on floats
    /// over whatever the user is reading, and three buttons is three times the surface for
    /// something whose whole job is to stay out of the way. Wrapping matters as much as
    /// cycling: a control that runs out at x2 leaves the user with no way back to x1
    /// except a trip to Ajustes, mid-reading.
    /// </para>
    /// </summary>
    public ReadingSpeed Next()
    {
        var index = All.ToList().IndexOf(this);

        // Defensive rather than theoretical: this record is public, so a value that is not
        // one of the three can be constructed, and IndexOf answers -1 for it. Starting the
        // cycle over is the only answer that leaves the user somewhere they can get out of.
        return index < 0 ? Normal : All[(index + 1) % All.Count];
    }
}

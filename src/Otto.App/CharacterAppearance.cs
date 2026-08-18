using System.Text.Json.Serialization;

namespace Otto.App;

/// <summary>
/// Which of the two overlays Otto floats on screen.
///
/// <para>
/// A separate choice from whether the overlay is shown at all, and deliberately so:
/// "I do not want Otto on screen" and "I want Otto on screen but not performing"
/// are different preferences, and collapsing them into one switch would force
/// anyone who found the character too much to give up the state indicator with it.
/// </para>
/// <para>
/// Serialised as a string rather than as the integer System.Text.Json would default
/// to, because the settings file is documented as something its owner can read and
/// edit by hand — and <c>"characterAppearance": 1</c> is not that.
/// </para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CharacterAppearance>))]
public enum CharacterAppearance
{
    /// <summary>The drawn character, 144×144, with poses and motion.</summary>
    Character,

    /// <summary>A dot and three lines, 64×24, that only change colour.</summary>
    Minimal,
}

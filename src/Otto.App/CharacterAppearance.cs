using System.Text.Json.Serialization;

namespace Otto.App;

/// <summary>
/// Which of the three overlays Otto floats on screen.
///
/// <para>
/// They are a progression rather than a menu: the character has a personality, the
/// discreet ring has a shape, the minimal glyph has a colour. Each one asks for
/// less of the user's attention than the last, and all three answer the same two
/// questions — is Otto on, and what is it doing.
/// </para>
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

    /// <summary>
    /// A ring with audio bars and no face. Drawn on the same 144×144 canvas as the
    /// character but shown at 60% of it, because four states told by colour do not
    /// need the room eight distinguishable poses do.
    /// </summary>
    Discreet,

    /// <summary>A dot and three lines, 64×24, that only change colour.</summary>
    Minimal,
}

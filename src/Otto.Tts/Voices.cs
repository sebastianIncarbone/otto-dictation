namespace Otto.Tts;

/// <summary>
/// One downloadable Piper voice.
///
/// <para>
/// A voice is two files, not one, and the pairing is load-bearing — see
/// <see cref="VoiceInstaller"/> for what happens when only the model arrives.
/// </para>
/// </summary>
public sealed record Voice(string Id, string Folder, string Quality, string Accent, string Description)
{
    private const string BaseUrl = "https://huggingface.co/rhasspy/piper-voices/resolve/main/es/";

    public string FileName => $"{Id}.onnx";

    public string ConfigFileName => FileName + ".json";

    public string Url => $"{BaseUrl}{Folder}/{Quality}/{FileName}";

    public string ConfigUrl => Url + ".json";

    /// <summary>
    /// The name inside the id — <c>daniela</c> out of <c>es_AR-daniela-high</c>.
    /// </summary>
    public string Name => Id.Split('-')[1];

    /// <summary>
    /// What the settings picker shows. Built from the id rather than stored beside it,
    /// for the reason the catalogue's missing size fields already state: a second copy of
    /// a fact can only ever drift from the first.
    /// </summary>
    public string Label => $"{char.ToUpperInvariant(Name[0])}{Name[1..]} · {Accent}";

    public string ModelPath(string directory) => Path.Combine(directory, FileName);

    public string ConfigPath(string directory) => Path.Combine(directory, ConfigFileName);

    /// <summary>
    /// Both files, or the voice is not installed. Checking only the model would call a
    /// half-finished download ready and then fail at synthesis time with a JSON parse
    /// error pointing nowhere near the real cause.
    /// </summary>
    public bool IsInstalled(string directory) =>
        File.Exists(ModelPath(directory)) && File.Exists(ConfigPath(directory));
}

/// <summary>
/// Every Spanish Piper voice, and the uncomfortable fact about the list.
///
/// <para>
/// Otto exists because Windows dictation is bad at Rioplatense. <c>CLAUDE.md</c> is
/// explicit that everything a user reads is in Rioplatense Spanish, and that an English
/// UI would contradict the product. A reading voice is that same argument with the
/// volume up: a tool built for how people here speak, answering in a Peninsular accent,
/// is the contradiction made audible.
/// </para>
/// <para>
/// And there is exactly one Argentine voice in the entire catalogue — <c>daniela</c>,
/// high quality, no other tier. So "configurable voices" is real, but it is a choice
/// between one voice that fits the product and five that do not. The others are offered
/// anyway, because a user who prefers a male voice or a Mexican accent is entitled to
/// that — but the default is never in question.
/// </para>
/// <para>
/// This is also why quality tiers cannot double as an effort ladder the way they could
/// in English. Piper ships voices at x_low/low/medium/high, and a lighter model is a
/// genuinely cheaper reading — but <c>daniela</c> exists only at <c>high</c>. Dropping a
/// tier means dropping the accent, which is not a trade this product gets to make. The
/// effort knob that survived is <see cref="PiperVoicing"/>, which changes how one model
/// is sampled rather than which model it is.
/// </para>
/// <para>
/// Sizes are deliberately absent. The download reports real bytes from
/// <c>Content-Length</c>, and a hardcoded "~110 MB" beside it would be a second source
/// of truth that can only ever drift — the same reason the release notes now derive
/// their hashes from the published <c>SHA256SUMS</c> instead of computing them twice.
/// </para>
/// </summary>
public static class Voices
{
    public static IReadOnlyList<Voice> All { get; } =
    [
        new("es_AR-daniela-high", "es_AR/daniela", "high", "Rioplatense",
            "La única voz argentina que existe. Es la que viene por defecto."),

        new("es_MX-claude-high", "es_MX/claude", "high", "Mexicano", "Masculina."),
        new("es_MX-ald-medium", "es_MX/ald", "medium", "Mexicano", "Más liviana."),

        new("es_ES-davefx-medium", "es_ES/davefx", "medium", "Peninsular", "Masculina."),
        new("es_ES-sharvard-medium", "es_ES/sharvard", "medium", "Peninsular", "Femenina."),
        new("es_ES-carlfm-x_low", "es_ES/carlfm", "x_low", "Peninsular", "La más chica de todas."),
    ];

    public static Voice Default => All[0];

    /// <summary>
    /// Resolves a stored setting back to a voice, falling back to <see cref="Default"/>
    /// rather than throwing.
    ///
    /// <para>
    /// Never throws on purpose, unlike the spike's console version. This reads a value
    /// out of <c>config.json</c>, which a user can edit and an older Otto can have
    /// written; a settings file naming a voice this build does not carry must not stop
    /// the app from starting. Falling back to the Argentine default is both the safe
    /// answer and the right one.
    /// </para>
    /// </summary>
    public static Voice Resolve(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? Default
            : All.FirstOrDefault(voice => voice.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? Default;

    /// <summary>The voices already on disk — what the settings picker offers without a download.</summary>
    public static IReadOnlyList<Voice> Installed(string directory) =>
        [.. All.Where(voice => voice.IsInstalled(directory))];
}

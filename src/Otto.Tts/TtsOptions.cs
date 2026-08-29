namespace Otto.Tts;

/// <summary>
/// Where the reading engine and its voices live on disk.
///
/// <para>
/// Paths only, and that is the point. The voice and the sampling preset are not here
/// even though they are obviously configuration, because they change while Otto is
/// running — the settings window has a voice picker — and this record is built once at
/// startup. Baking a runtime-changeable choice into a startup-time record is the exact
/// trap <c>Otto.Speech.ProvisioningOptions.CorrectionCoordinates</c> documents at length:
/// it made turning correction back on impossible until the decision was split in two.
/// The mutable half lives on <see cref="PiperSynthesizer"/> instead.
/// </para>
/// </summary>
public sealed record TtsOptions
{
    /// <summary>
    /// The folder holding <c>piper.exe</c> <b>and</b> its <c>espeak-ng-data</c>, which is
    /// not a coincidence — see <see cref="PiperSynthesizer"/> for why the two cannot be
    /// separated.
    /// </summary>
    public required string EngineDirectory { get; init; }

    /// <summary>
    /// Where downloaded voices land. Separate from <see cref="EngineDirectory"/> because
    /// they have different lifetimes: the engine ships with Otto and is replaced by the
    /// installer, the voices are user data and must survive an upgrade.
    /// </summary>
    public required string VoicesDirectory { get; init; }

    public string ExecutableName { get; init; } = "piper.exe";

    public string ExecutablePath => Path.Combine(EngineDirectory, ExecutableName);

    /// <summary>
    /// How long one fragment may take before the render is abandoned and the child
    /// process killed.
    ///
    /// <para>
    /// Generous on purpose. Piper renders a 300-character fragment in well under a second
    /// on the machines this was measured on, so thirty seconds is not a performance
    /// budget — it is the difference between a wedged child process and a reading feature
    /// that hangs forever with no way out. The user-visible stop button is the real
    /// control; this is the backstop for the case where nobody is watching.
    /// </para>
    /// </summary>
    public TimeSpan RenderTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

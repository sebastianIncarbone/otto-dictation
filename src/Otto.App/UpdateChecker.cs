using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Otto.App;

public sealed record UpdateStatus(bool Available, string CurrentVersion, string? LatestVersion, string? Url)
{
    public static UpdateStatus UpToDate(string current) => new(false, current, current, null);
}

/// <summary>
/// Looks for a newer release on GitHub.
///
/// <para>
/// This is the one feature in Otto that touches the internet after setup, and that
/// makes it a threat to the only claim the product really makes. "Works offline"
/// stops being true the moment something phones home at startup, no matter how
/// small the request or how good the reason.
/// </para>
/// <para>
/// So the check never runs on its own unless the user turns it on, and it is off
/// out of the box. The button in settings is always there, because someone
/// choosing to look is not the same as an application deciding to.
/// </para>
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    /// <summary>Dueño y nombre del repositorio. Un solo lugar para cambiarlo.</summary>
    public const string Repository = "sebastianIncarbone/otto-dictation";

    private const string LatestReleaseUrl = $"https://api.github.com/repos/{Repository}/releases/latest";

    private readonly HttpClient http;
    private readonly ILogger<UpdateChecker> log;

    public UpdateChecker(ILogger<UpdateChecker> log)
    {
        this.log = log;

        http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Otto/{Current}");
    }

    /// <summary>
    /// Sale de <c>InformationalVersion</c>, no de <c>AssemblyVersion</c>: la
    /// primera conserva lo que se puso en &lt;Version&gt;, mientras que la segunda
    /// recorta a cuatro números y pierde cualquier sufijo. .NET le agrega el hash
    /// de git detrás de un "+", que se descarta acá.
    /// </summary>
    public static string Current =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "0.0.0";

    public async Task<UpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var release = await http.GetFromJsonAsync<Release>(LatestReleaseUrl, cancellationToken);

            if (release?.Tag is null) return UpdateStatus.UpToDate(Current);

            var latest = release.Tag.TrimStart('v', 'V');

            return IsNewer(latest, Current)
                ? new UpdateStatus(true, Current, latest, release.Url)
                : UpdateStatus.UpToDate(Current);
        }
        catch (Exception ex)
        {
            // Being offline is the normal state for this application, not an error.
            log.LogInformation(ex, "No se pudo consultar si hay actualizaciones");
            return UpdateStatus.UpToDate(Current);
        }
    }

    /// <summary>
    /// Compares numerically rather than as strings, so 0.10.0 is correctly newer
    /// than 0.9.0 — the classic way a naive comparison starts lying at the tenth
    /// release and nobody notices for months.
    /// </summary>
    public static bool IsNewer(string candidate, string current)
    {
        if (!Version.TryParse(Pad(candidate), out var a)) return false;
        if (!Version.TryParse(Pad(current), out var b)) return false;

        return a > b;

        static string Pad(string value)
        {
            var parts = value.Split('-')[0].Split('.');
            return string.Join('.', Enumerable.Range(0, 3).Select(i => i < parts.Length ? parts[i] : "0"));
        }
    }

    private sealed record Release(
        [property: JsonPropertyName("tag_name")] string? Tag,
        [property: JsonPropertyName("html_url")] string? Url);

    public void Dispose() => http.Dispose();
}

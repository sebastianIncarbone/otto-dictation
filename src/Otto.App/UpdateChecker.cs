using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Otto.App;

public enum UpdateResult { UpToDate, Available, CouldNotCheck }

/// <summary>
/// Everything needed to fetch and check one release's installer, or null when the
/// release does not carry both halves.
///
/// <para>
/// Both halves, because a download URL on its own is not enough to act on. See
/// <see cref="UpdateInstaller"/> for what the checksum does and — more importantly —
/// what it does not do.
/// </para>
/// </summary>
public sealed record InstallerAsset(string Url, long Bytes, string ChecksumsUrl);

public sealed record UpdateStatus(
    UpdateResult Result,
    string CurrentVersion,
    string? LatestVersion,
    string? Url,
    InstallerAsset? Installer = null)
{
    public static UpdateStatus UpToDate(string current) => new(UpdateResult.UpToDate, current, current, null);

    /// <summary>
    /// Distinct from "you are up to date", deliberately.
    ///
    /// Not being able to find out — no network, private repository, GitHub down —
    /// is not the same as knowing there is nothing new. Returning the latter when
    /// the former happened is exactly the silent lie the version number already
    /// cost us once: it never fails, never warns, and you never find out.
    /// </summary>
    public static UpdateStatus CouldNotCheck(string current) =>
        new(UpdateResult.CouldNotCheck, current, null, null);

    /// <summary>
    /// Whether this release can be installed from inside Otto, as opposed to only
    /// linked to. False is the ordinary answer for every release published before
    /// the checksums asset existed, and the UI has to keep working for those.
    /// </summary>
    public bool CanInstall => Result == UpdateResult.Available && Installer is not null;
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
    /// <summary>Owner and repository name. One place to change it.</summary>
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
    /// Read from <c>InformationalVersion</c>, not <c>AssemblyVersion</c>: the
    /// first keeps whatever was put in &lt;Version&gt;, while the second truncates
    /// to four numbers and loses any suffix. .NET appends the git hash after a
    /// "+", which is discarded here.
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

            if (release?.Tag is null) return UpdateStatus.CouldNotCheck(Current);

            var latest = release.Tag.TrimStart('v', 'V');

            return IsNewer(latest, Current)
                ? new UpdateStatus(UpdateResult.Available, Current, latest, release.Url, InstallerFrom(release.Assets))
                : UpdateStatus.UpToDate(Current);
        }
        catch (Exception ex)
        {
            // Being offline is this application's normal state, not an error —
            // but it is not evidence of being up to date either.
            log.LogInformation(ex, "Could not check for updates");
            return UpdateStatus.CouldNotCheck(Current);
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

    /// <summary>
    /// Picks the installer and its checksums file out of a release's attachments,
    /// and answers null unless <b>both</b> are present.
    ///
    /// <para>
    /// Failing closed is the whole point. Every release published before this
    /// feature existed carries the installer but no <c>SHA256SUMS</c>, and so does
    /// any future one where that upload step breaks. Offering to install a file
    /// whose expected hash is unknown would mean running a downloaded executable on
    /// nothing but hope, which is worse than the link this falls back to — the link
    /// at least lands the user on the release page, where the hash is printed and
    /// Windows will still put SmartScreen in front of them.
    /// </para>
    /// <para>
    /// Names are matched exactly and case-sensitively against what
    /// <c>build/publicar.ps1</c> and the release workflow produce. A fuzzy match
    /// here would be a way to download the wrong file.
    /// </para>
    /// </summary>
    public static InstallerAsset? InstallerFrom(IReadOnlyList<ReleaseAsset>? assets)
    {
        if (assets is null) return null;

        var setup = assets.FirstOrDefault(a => a.Name == UpdateInstaller.InstallerAssetName);
        var checksums = assets.FirstOrDefault(a => a.Name == UpdateInstaller.ChecksumsAssetName);

        if (setup?.Url is null || checksums?.Url is null) return null;

        return new InstallerAsset(setup.Url, setup.Bytes, checksums.Url);
    }

    private sealed record Release(
        [property: JsonPropertyName("tag_name")] string? Tag,
        [property: JsonPropertyName("html_url")] string? Url,
        [property: JsonPropertyName("assets")] IReadOnlyList<ReleaseAsset>? Assets);

    public void Dispose() => http.Dispose();
}

/// <summary>One file attached to a GitHub release. Public only so the choice of
/// which attachment to trust is testable without a network.</summary>
public sealed record ReleaseAsset(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("browser_download_url")] string? Url,
    [property: JsonPropertyName("size")] long Bytes);

using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Otto.App;

/// <summary>
/// How far an install attempt got. Separate cases rather than a bool for the same
/// reason <see cref="UpdateResult"/> has three: "it did not work" is not one thing,
/// and the difference between "GitHub was unreachable" and "the file that arrived
/// was not the file that was published" is the difference between shrugging and
/// worrying.
/// </summary>
public enum UpdateInstallResult
{
    /// <summary>The installer is running. Otto is expected to exit right after this.</summary>
    Started,

    /// <summary>This is the portable copy; there is nothing here for an installer to update.</summary>
    NotInstalled,

    /// <summary>The release did not publish both the installer and its checksums.</summary>
    Unverifiable,

    /// <summary>Network, disk, or a truncated response.</summary>
    DownloadFailed,

    /// <summary>The bytes that arrived are not the bytes that were published.</summary>
    ChecksumMismatch,

    /// <summary>Downloaded and verified, but Windows would not start it.</summary>
    CouldNotLaunch,
}

/// <summary>
/// Downloads a published release's installer, checks it against the hash published
/// beside it, and hands it to Inno Setup in silent mode.
///
/// <para>
/// This is by a distance the most dangerous thing Otto does. Everything else it
/// touches is its own data; this fetches an executable off the internet and runs
/// it. So the constraints are written down here rather than assumed.
/// </para>
///
/// <para>
/// <b>What the checksum buys, and what it does not.</b> It catches a truncated
/// download, a corrupted one, a proxy that served something else, and a mismatched
/// pair of assets. It is <b>not</b> a signature: it is fetched from the same host,
/// over the same connection, as the file it describes, so anyone able to replace
/// the installer could replace the hash beside it. The actual integrity guarantee
/// here is TLS to GitHub. Saying "verified" and meaning "signed" would be the kind
/// of claim that reads as security and is not, so it is spelled out instead. The
/// real fix is code signing, which is unsolved and documented as such.
/// </para>
///
/// <para>
/// <b>Why this never runs on its own.</b> <see cref="UpdateChecker"/> already
/// explains why the check is manual and off by default. Downloading ~58 MB and
/// executing it is a larger act than reading a version string, not a smaller one,
/// so it inherits that rule and tightens it: the user asks for the check, and then
/// asks again for the install. Two deliberate clicks, no background path.
/// </para>
///
/// <para>
/// <b>Why only the installed copy.</b> The portable ZIP has no installer to run and
/// no place to put one, and a program replacing its own running executable in place
/// is a mess that fails halfway and leaves nothing runnable. The distributions are
/// told apart exactly as <see cref="Uninstaller"/> already tells them apart — by
/// Inno's own registry key — so the two cannot drift into disagreeing about which
/// copy this is.
/// </para>
/// </summary>
public sealed class UpdateInstaller : IDisposable
{
    /// <summary>Matches <c>OutputBaseFilename</c> in <c>build/otto.iss</c>.</summary>
    public const string InstallerAssetName = "Otto-Setup.exe";

    /// <summary>Matches the checksums file the release workflow uploads.</summary>
    public const string ChecksumsAssetName = "SHA256SUMS";

    /// <summary>
    /// <c>/SILENT</c> rather than <c>/VERYSILENT</c> on purpose: it shows a progress
    /// bar and nothing else. Otto's own window is about to vanish, and a user who
    /// clicked "instalar" and then watched the application disappear behind no
    /// feedback at all would reasonably conclude it had crashed.
    ///
    /// <para>
    /// <c>/CLOSEAPPLICATIONS</c> pairs with <c>CloseApplications=yes</c> in the
    /// script. Otto exits on its own immediately after this, so in practice there is
    /// nothing left to close — it is here for the case where exiting is slower than
    /// Inno reaching the file it needs to overwrite.
    /// </para>
    /// <para>
    /// <c>/SUPPRESSMSGBOXES</c> is deliberately absent. It makes Inno answer <i>yes</i>
    /// to Yes/No prompts, which the uninstall path documents as the reason it never
    /// asks about user data when silent. Nothing on the install path is known to
    /// prompt, but passing a flag whose meaning is "agree to whatever you are asked"
    /// to a process that is about to replace the application is not a trade worth
    /// making for a tidier screen.
    /// </para>
    /// </summary>
    internal const string SilentArguments = "/SILENT /CLOSEAPPLICATIONS";

    private readonly HttpClient http;
    private readonly string directory;
    private readonly ILogger<UpdateInstaller> log;

    public UpdateInstaller(string dataDirectory, ILogger<UpdateInstaller> log)
    {
        this.log = log;

        // Beside the models and the database, under %LOCALAPPDATA%\Otto, so
        // Uninstaller.Locations() already sweeps it up and a half-finished download
        // cannot outlive the application that made it.
        directory = Path.Combine(dataDirectory, "updates");

        // No timeout on the client itself: this is a ~58 MB download on whatever
        // connection the user has, and UpdateChecker's 10 s ceiling — right for a
        // JSON request — would abort a perfectly healthy transfer on a slow line.
        // Cancellation is the caller's, through the token.
        http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Otto/{UpdateChecker.Current}");
    }

    /// <summary>
    /// Whether this copy of Otto can update itself at all. False for the portable
    /// ZIP, which keeps the behaviour it has always had: a link to the release.
    /// </summary>
    public static bool CanSelfInstall => Uninstaller.InstalledUninstaller() is not null;

    /// <summary>
    /// Downloads, verifies and starts the installer. On <see cref="UpdateInstallResult.Started"/>
    /// the caller must shut Otto down — Inno is about to overwrite the executable
    /// this call returned into.
    /// </summary>
    /// <param name="asset">From <see cref="UpdateStatus.Installer"/>.</param>
    /// <param name="progress">0 to 100. Reported off the download thread.</param>
    public async Task<UpdateInstallResult> InstallAsync(
        InstallerAsset? asset,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!CanSelfInstall) return UpdateInstallResult.NotInstalled;
        if (asset is null) return UpdateInstallResult.Unverifiable;

        string setupPath;

        try
        {
            Directory.CreateDirectory(directory);

            var expected = await ExpectedHashAsync(asset.ChecksumsUrl, cancellationToken);

            // Fetched before the 58 MB, not after. Discovering that the checksums
            // file is unreadable is cheap now and expensive once the download has
            // already finished, and there is no point spending someone's data on a
            // file that could not have been checked anyway.
            if (expected is null)
            {
                log.LogWarning("The release published no usable hash for {Asset}; not installing", InstallerAssetName);
                return UpdateInstallResult.Unverifiable;
            }

            setupPath = await DownloadAsync(asset, progress, cancellationToken);

            var actual = await ComputeHashAsync(setupPath, cancellationToken);

            if (!HashesMatch(expected, actual))
            {
                // Deleted rather than kept for inspection. A rejected installer left
                // on disk is an executable that failed its only check sitting in a
                // folder the user might later open, and the log line is the part
                // worth keeping.
                log.LogError(
                    "The downloaded installer does not match the published hash (expected {Expected}, got {Actual}); deleting it",
                    expected, actual);

                TryDelete(setupPath);
                return UpdateInstallResult.ChecksumMismatch;
            }
        }
        // Only a cancellation the CALLER asked for gets to propagate. The filter is
        // load-bearing: ExpectedHashAsync imposes a deadline of its own, and that
        // deadline expiring also surfaces as OperationCanceledException. Rethrowing
        // it unconditionally would report an internal timeout as "the user cancelled",
        // which is a lie in the one direction that matters — the caller would clear
        // the status line instead of saying the update could not be fetched.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not download the update");
            return UpdateInstallResult.DownloadFailed;
        }

        return Launch(setupPath) ? UpdateInstallResult.Started : UpdateInstallResult.CouldNotLaunch;
    }

    private async Task<string> DownloadAsync(
        InstallerAsset asset,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, InstallerAssetName);

        using var response = await http.GetAsync(
            asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        response.EnsureSuccessStatusCode();

        // The release's own figure is preferred over Content-Length: a redirect to
        // object storage can answer without one, and progress that silently stops
        // moving is worse than progress that was never offered.
        var total = response.Content.Headers.ContentLength ?? asset.Bytes;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);

        // FileMode.Create, so a previous attempt's leftovers are overwritten rather
        // than appended to — appending would produce a file that fails its hash for
        // a reason nobody would guess from the message.
        await using var destination = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);

        var buffer = new byte[81920];
        long written = 0;
        var lastReported = -1;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            written += read;

            if (total <= 0) continue;

            // Only on change, so a 58 MB download raises ~100 events and not ~700.
            var percent = (int)(written * 100 / total);

            if (percent != lastReported)
            {
                lastReported = percent;
                progress?.Report(percent);
            }
        }

        // A short file is a broken connection, and it must not reach the hash check.
        //
        // It would fail there — a truncated file does not hash to the published
        // value — but it would fail as ChecksumMismatch, and these two are not the
        // same news. "The bytes that arrived are not the bytes that were published"
        // is alarming and unfixable by trying again; "the download was cut off" is
        // ordinary and fixed by exactly that. Sorting them here is also what lets
        // the view model offer a retry for one and refuse it for the other.
        if (total > 0 && written != total)
        {
            throw new IOException(
                $"The download ended early: {written} of {total} bytes. The connection was probably cut.");
        }

        log.LogInformation("Downloaded {Bytes} bytes to {Path}", written, path);

        return path;
    }

    /// <summary>
    /// The checksums file gets its own deadline.
    ///
    /// <para>
    /// The client is built with no timeout because a ~58 MB transfer on a slow line
    /// is not a stuck one, and <see cref="UpdateChecker"/>'s 10 s — right for a JSON
    /// request — would abort healthy downloads. That reasoning does not extend to
    /// this request: it is a few hundred bytes, so a slow one is a hung one, and
    /// without a deadline of its own it would inherit "wait forever" from a setting
    /// that exists for a completely different request.
    /// </para>
    /// </summary>
    private async Task<string?> ExpectedHashAsync(string checksumsUrl, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));

        var body = await http.GetStringAsync(checksumsUrl, deadline.Token);

        return HashFor(body, InstallerAssetName);
    }

    /// <summary>
    /// Reads one file's hash out of <c>sha256sum</c> output.
    ///
    /// <para>
    /// The format is <c>&lt;64 hex&gt;&lt;space&gt;&lt;space or *&gt;&lt;name&gt;</c>,
    /// where the second marker is a space for text mode and an asterisk for binary.
    /// Both are accepted because which one appears depends on how the file was
    /// produced, and a checksums file that parses on the build machine but not on
    /// the user's would fail closed for a reason nobody could see.
    /// </para>
    /// <para>
    /// Returns null for anything it cannot read with certainty. There is no partial
    /// success here: an unparsed line has to mean "no hash", never "assume it fits".
    /// </para>
    /// </summary>
    public static string? HashFor(string checksums, string fileName)
    {
        foreach (var line in checksums.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            var separator = trimmed.IndexOf(' ');
            if (separator <= 0) continue;

            var hash = trimmed[..separator];
            var name = trimmed[(separator + 1)..].TrimStart(' ', '*');

            if (name == fileName && IsSha256(hash)) return hash;
        }

        return null;
    }

    /// <summary>
    /// Compares two hex digests.
    ///
    /// <para>
    /// The length check is not decoration. Without it an empty, truncated or
    /// otherwise unparsed <paramref name="expected"/> compares equal to an equally
    /// empty <paramref name="actual"/>, and the one gate standing between a
    /// downloaded file and being executed passes by accident. Anything that is not
    /// a full SHA-256 is a mismatch.
    /// </para>
    /// </summary>
    public static bool HashesMatch(string? expected, string? actual) =>
        IsSha256(expected) && IsSha256(actual) &&
        string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);

        var digest = await SHA256.HashDataAsync(stream, cancellationToken);

        return Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// Starts the installer and leaves. <c>UseShellExecute</c> is false so this is a
    /// direct <c>CreateProcess</c> — the file was written by <see cref="HttpClient"/>
    /// and therefore carries no Mark of the Web, which is what SmartScreen keys off.
    /// </summary>
    private bool Launch(string setupPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(setupPath, SilentArguments) { UseShellExecute = false });

            log.LogInformation("Started {Path} {Arguments}", setupPath, SilentArguments);

            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not start the downloaded installer");
            return false;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not delete {Path}", path);
        }
    }

    public void Dispose() => http.Dispose();
}

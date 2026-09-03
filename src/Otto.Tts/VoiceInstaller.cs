using Microsoft.Extensions.Logging;

namespace Otto.Tts;

/// <summary>
/// How far a voice download has got.
///
/// <para>
/// Deliberately its own type rather than a shared one, even though
/// <c>Otto.Speech.DownloadProgress</c> has the same three fields. <c>Otto.Speech</c> and
/// <c>Otto.Tts</c> are two adapters behind two different ports; an adapter reaching into
/// another adapter's types to save a record declaration is how a hexagon quietly turns
/// into a ball of mud, and it would drag Whisper.net and three native runtime packages
/// into a project whose entire point is that it needs none of them. The composition root
/// is the layer allowed to know about both.
/// </para>
/// </summary>
public sealed record VoiceDownloadProgress(long Downloaded, long Total, double BytesPerSecond)
{
    public double Fraction => Total > 0 ? Downloaded / (double)Total : 0;

    public TimeSpan? Remaining => BytesPerSecond > 1 && Total > 0
        ? TimeSpan.FromSeconds((Total - Downloaded) / BytesPerSecond)
        : null;
}

/// <summary>
/// Where voice files come from.
///
/// <para>
/// Split out for the same reason <c>Otto.Speech.IModelSource</c> is: the install logic —
/// which files, in which order, and what counts as finished — has to be exercisable
/// without a network.
/// </para>
/// <para>
/// Two obligations on the adapter, both inherited from the speech downloader because a
/// user losing a 110 MB transfer at 100 MB has exactly the same reaction as one losing
/// 1,6 GB at 1,4: leave a resumable partial behind on failure, and move the final file
/// into place only once the transfer is complete. A truncated file that looks finished
/// surfaces much later as a corrupt model at synthesis time, with nothing pointing back
/// at the download.
/// </para>
/// </summary>
public interface IVoiceSource
{
    Task FetchAsync(string url, string destination,
        IProgress<VoiceDownloadProgress>? progress, CancellationToken cancellationToken = default);
}

/// <summary>
/// Gets a voice onto the disk.
///
/// <para>
/// Not part of startup provisioning, and that is a decision rather than an omission.
/// <c>ModelProvisioner</c> runs before the pipeline exists and holds the window on a
/// progress card while it works, which is right for the models Otto cannot dictate
/// without. Reading is opt-in and off by default; adding a fourth leg there would hang
/// every first run on a 110 MB download for a feature the user has not asked for. So a
/// voice arrives when somebody turns reading on, and the wait belongs to the settings
/// window that asked for it.
/// </para>
/// </summary>
public sealed class VoiceInstaller(TtsOptions options, IVoiceSource source, ILogger<VoiceInstaller> log)
{
    public bool IsInstalled(Voice voice) => voice.IsInstalled(options.VoicesDirectory);

    /// <summary>
    /// Downloads both halves of a voice. Idempotent: a voice already on disk returns
    /// immediately, so this is safe to call on every settings save.
    /// </summary>
    public async Task InstallAsync(
        Voice voice, IProgress<VoiceDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (IsInstalled(voice))
        {
            log.LogDebug("The voice {Voice} is already installed", voice.Id);
            return;
        }

        Directory.CreateDirectory(options.VoicesDirectory);

        log.LogInformation("Downloading the voice {Voice} ({Accent})", voice.Id, voice.Accent);

        // The config goes first, and it is worth a sentence. It is a few kilobytes against
        // the model's hundred-odd megabytes, so a wrong URL, a moved repository or a
        // captive-portal login page comes back in a millisecond instead of after a long
        // download that was never going to work. Piper also resolves the config from the
        // model path and fails with a JSON parse error rather than a missing-file error
        // when it is absent, which sends anyone debugging it to entirely the wrong place —
        // fetching it first means that state is never reached by this path.
        await source.FetchAsync(voice.ConfigUrl, voice.ConfigPath(options.VoicesDirectory), null, cancellationToken);
        await source.FetchAsync(voice.Url, voice.ModelPath(options.VoicesDirectory), progress, cancellationToken);

        // The post-condition callers trust before switching reading on. An adapter that
        // reported success while leaving one half behind would produce a voice that passes
        // every settings check and then fails at the first fragment.
        if (!IsInstalled(voice))
            throw new IOException($"The voice {voice.Id} reported a complete download but is not on disk.");

        log.LogInformation("The voice {Voice} is ready", voice.Id);
    }
}

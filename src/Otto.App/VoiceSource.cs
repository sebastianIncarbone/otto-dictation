using Microsoft.Extensions.Logging;
using Otto.Speech;
using Otto.Tts;

namespace Otto.App;

/// <summary>
/// Downloads a voice with the same machinery that downloads the speech model.
///
/// <para>
/// This class exists because <c>Otto.Tts</c> and <c>Otto.Speech</c> are two adapters
/// behind two ports and neither may reference the other — referencing <c>Otto.Speech</c>
/// from the reading project would drag Whisper.net and three native runtime packages into
/// a project whose entire point is that it needs none of them. The composition root is the
/// one layer allowed to know about both, so the join happens here, in ten lines, instead
/// of by widening a dependency.
/// </para>
/// <para>
/// What is being reused is worth naming: <see cref="ModelDownloader"/> resumes. A voice is
/// ~110 MB rather than the speech model's 1,6 GB, but somebody who loses it at 100 MB has
/// exactly the same reaction as somebody who loses 1,6 GB at 1,4 — and writing a second,
/// simpler downloader here would be choosing to lose that.
/// </para>
/// </summary>
internal sealed class VoiceSource(ILogger<ModelDownloader> log) : IVoiceSource
{
    public async Task FetchAsync(string url, string destination,
        IProgress<VoiceDownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        using var downloader = new ModelDownloader(log);

        await downloader.DownloadAsync(
            url, destination, progress is null ? null : new Bridge(progress), cancellationToken);
    }

    /// <summary>
    /// Carries byte-level progress across the boundary. The two records have the same
    /// three fields on purpose and stay separate on purpose; this is the seam where that
    /// decision gets paid for, and it is one line.
    /// </summary>
    private sealed class Bridge(IProgress<VoiceDownloadProgress> inner) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) =>
            inner.Report(new VoiceDownloadProgress(value.Downloaded, value.Total, value.BytesPerSecond));
    }
}

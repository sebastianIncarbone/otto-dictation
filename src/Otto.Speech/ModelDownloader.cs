using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace Otto.Speech;

public sealed record DownloadProgress(long Downloaded, long Total, double BytesPerSecond)
{
    public double Fraction => Total > 0 ? Downloaded / (double)Total : 0;

    public TimeSpan? Remaining => BytesPerSecond > 1 && Total > 0
        ? TimeSpan.FromSeconds((Total - Downloaded) / BytesPerSecond)
        : null;
}

/// <summary>
/// Downloads the speech model, and survives losing the connection.
///
/// This is the user's first contact with Otto and it moves 1,6 GB. Losing it at
/// 1,4 GB and having to start over is a perfectly good reason to delete an
/// application you have not used yet, so the transfer resumes instead: the partial
/// file stays on disk and the next attempt continues from its length with a Range
/// request.
/// </summary>
public sealed class ModelDownloader : IDisposable
{
    private const string BaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    private readonly HttpClient http;
    private readonly ILogger<ModelDownloader> log;

    public ModelDownloader(ILogger<ModelDownloader> log)
    {
        this.log = log;

        http = new HttpClient
        {
            // Long by design: this is one very large transfer, not an API call. The
            // per-read cancellation token is what actually bounds it.
            Timeout = TimeSpan.FromHours(2),
        };
    }

    public async Task DownloadAsync(
        string fileName,
        string destination,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(destination)) return;

        // The partial lives next to the final name so an interrupted download is
        // obvious on disk, and so a truncated file can never be mistaken for a
        // complete model — that failure surfaces much later, as a corrupt model at
        // load time, with nothing pointing back at the download.
        var partial = destination + ".part";
        var already = File.Exists(partial) ? new FileInfo(partial).Length : 0;

        if (already > 0)
            log.LogInformation("Reanudando descarga de {File} desde {Mb:N0} MB", fileName, already / 1024d / 1024);

        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + fileName);
        if (already > 0) request.Headers.Range = new RangeHeaderValue(already, null);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        // A server that ignores Range answers 200 with the whole file; continuing
        // to append would corrupt it, so start over instead.
        if (already > 0 && response.StatusCode != HttpStatusCode.PartialContent)
        {
            log.LogWarning("El servidor no admite reanudar; se descarga de nuevo desde cero");
            File.Delete(partial);
            already = 0;
        }

        response.EnsureSuccessStatusCode();

        var total = (response.Content.Headers.ContentLength ?? 0) + already;

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var file = new FileStream(partial, already > 0 ? FileMode.Append : FileMode.Create,
                     FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await CopyAsync(source, file, already, total, progress, cancellationToken);
        }

        var finalSize = new FileInfo(partial).Length;

        if (total > 0 && finalSize != total)
            throw new IOException(
                $"La descarga de {fileName} quedó incompleta: {finalSize:N0} de {total:N0} bytes. " +
                "El archivo parcial queda en disco, así que reintentar continúa desde ahí.");

        File.Move(partial, destination, overwrite: true);
        log.LogInformation("{File} listo ({Mb:N0} MB)", fileName, finalSize / 1024d / 1024);
    }

    private static async Task CopyAsync(
        Stream source,
        Stream destination,
        long already,
        long total,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var downloaded = already;

        var started = Environment.TickCount64;
        var lastReport = started;

        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;

            // Throttled: a progress event per 80 KB chunk would spend more time
            // marshalling to the UI thread than downloading.
            var now = Environment.TickCount64;
            if (now - lastReport < 250) continue;

            var elapsed = Math.Max(1, now - started) / 1000d;
            progress?.Report(new DownloadProgress(downloaded, total, (downloaded - already) / elapsed));
            lastReport = now;
        }

        progress?.Report(new DownloadProgress(downloaded, total, 0));
    }

    public void Dispose() => http.Dispose();
}

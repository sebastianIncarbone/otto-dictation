using Microsoft.Extensions.Logging;
using Whisper.net.Ggml;

namespace Otto.Speech;

public enum ProvisioningState { Idle, DownloadingSpeech, PreparingVad, Ready, Failed }

/// <summary>Progress is null except while a leg is transferring.</summary>
public sealed record ProvisioningStatus(ProvisioningState State, DownloadProgress? Progress = null);

/// <summary>
/// Where the two model files come from. Split out so the provisioning state machine
/// is exercisable without a network: the adapter is obliged to leave a resumable
/// partial behind on failure, and to move the final file into place only once the
/// transfer is complete.
/// </summary>
public interface IModelSource
{
    Task FetchSpeechAsync(string fileName, string destination,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken = default);

    Task FetchVadAsync(string destination, CancellationToken cancellationToken = default);
}

/// <summary>
/// The production <see cref="IModelSource"/>. The speech model comes from
/// <see cref="ModelDownloader"/> — resumable, progress-reporting, ~1,6 GB on the
/// GPU path. The VAD model comes from Whisper.net's own downloader instead: it is a
/// single megabyte, so resuming it buys nothing, and the library already knows
/// where to fetch it from.
/// </summary>
public sealed class HuggingFaceModelSource(ILogger<ModelDownloader> log) : IModelSource
{
    public async Task FetchSpeechAsync(string fileName, string destination,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        using var downloader = new ModelDownloader(log);
        await downloader.DownloadAsync(fileName, destination, progress, cancellationToken);
    }

    public async Task FetchVadAsync(string destination, CancellationToken cancellationToken = default)
    {
        var stream = await WhisperGgmlDownloader.Default.GetGgmlSileroVadModelAsync(SileroVadType.V5_1_2, cancellationToken);

        // Same convention as ModelDownloader: land in a .part next to the final
        // name so a process killed mid-write can never leave behind something that
        // looks like a finished model.
        var temp = destination + ".part";

        await using (var file = File.Create(temp))
            await stream.CopyToAsync(file, cancellationToken);

        File.Move(temp, destination, overwrite: true);
    }
}

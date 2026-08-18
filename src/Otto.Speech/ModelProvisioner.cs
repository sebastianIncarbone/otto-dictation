using Microsoft.Extensions.Logging;

namespace Otto.Speech;

/// <summary>
/// Are the two model files there, and if not, get them.
///
/// Lives here rather than behind a port in <c>Otto.Core</c>: nothing in the
/// orchestration layer needs to know that a Whisper model arrives in two legs from
/// two hosts — that is exactly what <c>ITranscriber.LoadAsync</c> exists to hide.
/// </summary>
public sealed class ModelProvisioner(ProvisioningOptions options, IModelSource source, ILogger<ModelProvisioner> log)
{
    /// <summary>
    /// Files, not <c>Settings.IsFirstRun</c>: deleting the models directory has to
    /// reproduce the download, and that user is not on a first run.
    /// </summary>
    public bool NeedsProvisioning => !File.Exists(options.SpeechPath) || !File.Exists(options.VadPath);

    // The DictationPipeline.busy pattern: two concurrent legs would open the same
    // .part file with FileShare.None and throw, so the second caller is turned away
    // instead of racing the first one to the same download.
    private int busy;

    /// <summary>
    /// Never throws. Returns <see cref="ProvisioningState.Ready"/> only once both
    /// files are verified on disk — that post-condition is what
    /// <c>App.StartPipeline()</c> trusts before loading them.
    /// </summary>
    public async Task<ProvisioningState> ProvisionAsync(
        IProgress<ProvisioningStatus>? progress, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return ProvisioningState.Idle;

        try
        {
            Directory.CreateDirectory(options.ModelsDirectory);

            if (!File.Exists(options.SpeechPath))
            {
                progress?.Report(new ProvisioningStatus(ProvisioningState.DownloadingSpeech));

                var speechProgress = progress is null ? null : new SpeechProgressAdapter(progress);
                await source.FetchSpeechAsync(options.SpeechFileName, options.SpeechPath, speechProgress, cancellationToken);
            }

            if (!File.Exists(options.VadPath))
            {
                progress?.Report(new ProvisioningStatus(ProvisioningState.PreparingVad));
                await source.FetchVadAsync(options.VadPath, cancellationToken);
            }

            if (!File.Exists(options.SpeechPath) || !File.Exists(options.VadPath))
            {
                log.LogError("Provisioning reported success but a model file is still missing");
                progress?.Report(new ProvisioningStatus(ProvisioningState.Failed));
                return ProvisioningState.Failed;
            }

            progress?.Report(new ProvisioningStatus(ProvisioningState.Ready));
            return ProvisioningState.Ready;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Quitting from the tray mid-download is not a failure: the .part file
            // is untouched on disk, and the next launch resumes from there. Caught
            // separately from the broad catch below so this case never reaches the
            // log as an error or flashes a Spanish failure while the app tears down.
            //
            // The token filter is load-bearing. Without it this arm caught *every*
            // OperationCanceledException, and HttpClient surfaces a timeout as
            // TaskCanceledException — an OperationCanceledException carrying none
            // of our tokens. So a timed-out download was read as "the user quit":
            // it returned Idle, reported nothing, and left the window on a frozen
            // progress card with no Reintentar, no notes, and no pipeline, escapable
            // only by killing the process. That is precisely the silent startup this
            // class exists to remove, so anything cancelled that we did not cancel
            // now falls through to the failure arm below, where the user can see it.
            return ProvisioningState.Idle;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not provision the speech models");
            progress?.Report(new ProvisioningStatus(ProvisioningState.Failed));
            return ProvisioningState.Failed;
        }
        finally
        {
            Interlocked.Exchange(ref busy, 0);
        }
    }

    /// <summary>
    /// Forwards the speech leg's byte-level progress into the same
    /// <see cref="ProvisioningStatus"/> stream everything else reports through,
    /// rather than constructing a second <see cref="Progress{T}"/> here: the outer
    /// <paramref name="progress"/> is the one built on the UI thread, and this keeps
    /// there being exactly one place responsible for the thread marshalling.
    /// </summary>
    private sealed class SpeechProgressAdapter(IProgress<ProvisioningStatus> progress) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) =>
            progress.Report(new ProvisioningStatus(ProvisioningState.DownloadingSpeech, value));
    }
}

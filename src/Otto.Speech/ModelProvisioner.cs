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
    ///
    /// The correction leg counts too, gated by exactly the same conditions
    /// <see cref="ProvisionAsync"/>'s own third leg checks before it will attempt a
    /// download — <see cref="ProvisioningOptions.HasGpu"/>, a configured
    /// <see cref="ProvisioningOptions.CorrectionFileName"/> AND a configured
    /// <see cref="ProvisioningOptions.CorrectionUrl"/>. All three, not a subset:
    /// an earlier version of this property checked only the first two, so a
    /// configuration that set a file name without a URL (or vice versa) left this
    /// permanently true for a leg <see cref="ProvisionAsync"/> would silently
    /// never download — an unrecoverable "needs provisioning forever" state, since
    /// nothing else re-checks it. Without the leg counting at all, an Ollama-era
    /// install that already has Whisper+VAD on disk would never trip provisioning
    /// for the GGUF — this file-existence check is the entire migration-detection
    /// mechanism for that population, so it has to know about every leg
    /// <see cref="ProvisionAsync"/> can actually download.
    /// </summary>
    public bool NeedsProvisioning =>
        !File.Exists(options.SpeechPath) || !File.Exists(options.VadPath)
        || (options.HasGpu && options.CorrectionFileName is not null && options.CorrectionUrl is not null
            && !File.Exists(options.CorrectionPath));

    /// <summary>
    /// Which leg <see cref="ProvisionAsync"/> will report first, mirroring its own
    /// check order exactly (speech, then VAD, then correction). Exists so a caller
    /// that needs to seed UI state BEFORE the first <see cref="IProgress{T}"/>
    /// report arrives — <c>App.axaml.cs</c>, on startup — can show the leg that is
    /// actually about to run instead of always assuming the speech model. An
    /// Ollama-era upgrade with Whisper+VAD already on disk starts on the
    /// correction leg instead, and seeding "descargando {Label}, solo la primera
    /// vez…" for that population is simply wrong — they are not on a first run.
    ///
    /// Meaningless when <see cref="NeedsProvisioning"/> is false (it still returns
    /// <see cref="ProvisioningState.DownloadingCorrection"/>, since nothing is
    /// missing); callers are expected to check that first, the same way
    /// <c>App.axaml.cs</c> already does before reading this property at all.
    /// </summary>
    public ProvisioningState InitialProvisioningState =>
        !File.Exists(options.SpeechPath) ? ProvisioningState.DownloadingSpeech
        : !File.Exists(options.VadPath) ? ProvisioningState.PreparingVad
        : ProvisioningState.DownloadingCorrection;

    // The DictationPipeline.busy pattern: two concurrent legs would open the same
    // .part file with FileShare.None and throw, so the second caller is turned away
    // instead of racing the first one to the same download.
    private int busy;

    /// <summary>
    /// Never throws. Returns <see cref="ProvisioningState.Ready"/> only once both
    /// files are verified on disk — that post-condition is what
    /// <c>App.StartPipelineAsync()</c> trusts before loading them.
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

            // Third leg, last, and non-fatal: correction is a nice-to-have on top
            // of a working dictation pipeline, not a requirement for one. A failed
            // ~2 GB GGUF download must not cost the user Whisper+VAD, which is why
            // this has its own try/catch instead of falling into the one below —
            // and it never runs at all without GPU acceleration, for the same
            // reason HardwareProbe already steers CPU-only machines away from
            // large-v3-turbo: a 3B model on CPU can never land inside the 2s
            // dictation budget, so downloading it there would only cost bandwidth
            // for a feature that can never work.
            if (options.HasGpu && options.CorrectionPath is { } correctionPath && options.CorrectionUrl is { } correctionUrl
                && !File.Exists(correctionPath))
            {
                try
                {
                    progress?.Report(new ProvisioningStatus(ProvisioningState.DownloadingCorrection));

                    var correctionProgress = progress is null ? null : new CorrectionProgressAdapter(progress);
                    await source.FetchAsync(correctionUrl, correctionPath, correctionProgress, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Our own token: let this propagate to the same "user quit,
                    // not a failure" handling as the speech/VAD legs below, rather
                    // than swallowing it here and reporting Ready for a
                    // provisioning run that was actually asked to stop.
                    throw;
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "No se pudo descargar el modelo de corrección; el dictado sigue funcionando sin ella");
                }
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

    /// <inheritdoc cref="SpeechProgressAdapter"/>
    private sealed class CorrectionProgressAdapter(IProgress<ProvisioningStatus> progress) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) =>
            progress.Report(new ProvisioningStatus(ProvisioningState.DownloadingCorrection, value));
    }
}

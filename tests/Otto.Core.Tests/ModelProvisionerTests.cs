using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Otto.Speech;

namespace Otto.Core.Tests;

/// <summary>
/// The provisioner exercised without a network: the seam is <see cref="IModelSource"/>,
/// so every scenario — success, failure, cancellation, a concurrent second call — is
/// reproducible without downloading anything.
/// </summary>
public sealed class ModelProvisionerTests : IDisposable
{
    private readonly string modelsDir = Directory.CreateTempSubdirectory("otto-provisioner-tests-").FullName;
    private readonly IModelSource source = Substitute.For<IModelSource>();

    private ProvisioningOptions Options() => new()
    {
        ModelsDirectory = modelsDir,
        SpeechFileName = "ggml-base.bin",
        VadFileName = "silero-vad.bin",
        Label = "base",
        Size = "~150 MB",
    };

    private ModelProvisioner Build(ProvisioningOptions options) =>
        new(options, source, NullLogger<ModelProvisioner>.Instance);

    public void Dispose() => Directory.Delete(modelsDir, recursive: true);

    [Fact]
    public void Detecta_que_faltan_los_modelos_cuando_no_estan_en_disco()
    {
        var provisioner = Build(Options());

        Assert.True(provisioner.NeedsProvisioning);
    }

    [Fact]
    public void No_pide_nada_si_ambos_modelos_existen()
    {
        var options = Options();
        File.WriteAllBytes(options.SpeechPath, []);
        File.WriteAllBytes(options.VadPath, []);

        var provisioner = Build(options);

        Assert.False(provisioner.NeedsProvisioning);
    }

    [Fact]
    public async Task Baja_el_habla_y_despues_el_detector_de_voz_y_termina_listo()
    {
        var options = Options();

        // The fake source is what stands in for the network: it does exactly what
        // the real one is obliged to do — write the file — so the post-condition
        // check inside ProvisionAsync has something real to find.
        source
            .FetchSpeechAsync(options.SpeechFileName, options.SpeechPath, Arg.Any<IProgress<DownloadProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { File.WriteAllBytes(options.SpeechPath, []); return Task.CompletedTask; });

        source
            .FetchVadAsync(options.VadPath, Arg.Any<CancellationToken>())
            .Returns(_ => { File.WriteAllBytes(options.VadPath, []); return Task.CompletedTask; });

        var reported = new List<ProvisioningState>();
        var progress = new RecordingProgress<ProvisioningStatus>(s => reported.Add(s.State));

        var result = await Build(options).ProvisionAsync(progress);

        Assert.Equal(ProvisioningState.Ready, result);
        Assert.Equal(
            [ProvisioningState.DownloadingSpeech, ProvisioningState.PreparingVad, ProvisioningState.Ready],
            reported);
    }

    [Fact]
    public async Task El_progreso_de_bytes_de_la_descarga_del_habla_llega_como_ProvisioningStatus()
    {
        // Pins down SpeechProgressAdapter: nothing else in this file invokes the
        // IProgress<DownloadProgress> handed to FetchSpeechAsync, so without this
        // test the adapter that turns byte-level progress into the outer
        // ProvisioningStatus stream never runs at all.
        var options = Options();

        source
            .FetchSpeechAsync(options.SpeechFileName, options.SpeechPath, Arg.Any<IProgress<DownloadProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.ArgAt<IProgress<DownloadProgress>?>(2)?.Report(new DownloadProgress(50, 100, 1_048_576));
                File.WriteAllBytes(options.SpeechPath, []);
                return Task.CompletedTask;
            });

        source
            .FetchVadAsync(options.VadPath, Arg.Any<CancellationToken>())
            .Returns(_ => { File.WriteAllBytes(options.VadPath, []); return Task.CompletedTask; });

        var reported = new List<ProvisioningStatus>();
        var progress = new RecordingProgress<ProvisioningStatus>(reported.Add);

        await Build(options).ProvisionAsync(progress);

        var withProgress = reported.Find(s => s.Progress is not null);

        Assert.NotNull(withProgress);
        Assert.Equal(ProvisioningState.DownloadingSpeech, withProgress!.State);
        Assert.Equal(0.5, withProgress.Progress!.Fraction);
        Assert.Equal(1_048_576, withProgress.Progress.BytesPerSecond);
    }

    [Fact]
    public async Task Si_ya_esta_el_habla_solo_pide_el_detector_de_voz()
    {
        var options = Options();
        File.WriteAllBytes(options.SpeechPath, []);

        source
            .FetchVadAsync(options.VadPath, Arg.Any<CancellationToken>())
            .Returns(_ => { File.WriteAllBytes(options.VadPath, []); return Task.CompletedTask; });

        var result = await Build(options).ProvisionAsync(progress: null);

        Assert.Equal(ProvisioningState.Ready, result);
        await source.DidNotReceive().FetchSpeechAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<DownloadProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Un_error_de_red_termina_en_Failed_y_no_propaga()
    {
        var options = Options();

        source
            .FetchSpeechAsync(options.SpeechFileName, options.SpeechPath, Arg.Any<IProgress<DownloadProgress>?>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new IOException("sin conexión"));

        var result = await Build(options).ProvisionAsync(progress: null);

        Assert.Equal(ProvisioningState.Failed, result);
    }

    [Fact]
    public async Task Falla_si_el_archivo_no_aparece_tras_la_descarga()
    {
        var options = Options();

        // The fake reports success without actually writing the file — a corrupt or
        // truncated transfer that somehow didn't throw. Ready has to mean the file
        // is really there, because StartPipeline() trusts it without checking again.
        source
            .FetchSpeechAsync(options.SpeechFileName, options.SpeechPath, Arg.Any<IProgress<DownloadProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await Build(options).ProvisionAsync(progress: null);

        Assert.Equal(ProvisioningState.Failed, result);
    }

    [Fact]
    public async Task Cancelar_desde_la_bandeja_no_se_reporta_como_error()
    {
        // The token matters, and passing it is the whole point of this test: only a
        // cancellation Otto itself asked for counts as "the user quit". An earlier
        // version threw a token-less exception here and still passed, which is what
        // let a network timeout take the quit path.
        var options = Options();

        using var shutdown = new CancellationTokenSource();
        await shutdown.CancelAsync();

        source
            .FetchSpeechAsync(options.SpeechFileName, options.SpeechPath, Arg.Any<IProgress<DownloadProgress>?>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new OperationCanceledException(shutdown.Token));

        var reported = new List<ProvisioningState>();
        var progress = new RecordingProgress<ProvisioningStatus>(s => reported.Add(s.State));

        var exception = await Record.ExceptionAsync(() => Build(options).ProvisionAsync(progress, shutdown.Token));

        Assert.Null(exception);
        Assert.DoesNotContain(ProvisioningState.Failed, reported);
    }

    [Fact]
    public async Task Un_timeout_de_red_se_reporta_como_error_y_no_como_cancelacion()
    {
        // HttpClient surfaces its own timeout as TaskCanceledException, which derives
        // from OperationCanceledException but carries none of Otto's tokens. Treated as
        // a user quit it returned Idle and reported nothing, leaving the window on a
        // frozen progress card with no Reintentar and no pipeline — the silent startup
        // this whole change exists to remove, reintroduced inside the fix for it.
        var options = Options();

        source
            .FetchSpeechAsync(options.SpeechFileName, options.SpeechPath, Arg.Any<IProgress<DownloadProgress>?>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout"));

        var reported = new List<ProvisioningState>();
        var progress = new RecordingProgress<ProvisioningStatus>(s => reported.Add(s.State));

        var result = await Build(options).ProvisionAsync(progress);

        Assert.Equal(ProvisioningState.Failed, result);
        Assert.Contains(ProvisioningState.Failed, reported);
    }

    [Fact]
    public async Task No_arranca_dos_provisiones_a_la_vez()
    {
        var options = Options();
        var release = new TaskCompletionSource();

        source
            .FetchSpeechAsync(options.SpeechFileName, options.SpeechPath, Arg.Any<IProgress<DownloadProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await release.Task;
                File.WriteAllBytes(options.SpeechPath, []);
            });

        source
            .FetchVadAsync(options.VadPath, Arg.Any<CancellationToken>())
            .Returns(_ => { File.WriteAllBytes(options.VadPath, []); return Task.CompletedTask; });

        var provisioner = Build(options);

        var first = provisioner.ProvisionAsync(progress: null);
        var second = await provisioner.ProvisionAsync(progress: null);

        release.SetResult();
        await first;

        await source.Received(1).FetchSpeechAsync(
            options.SpeechFileName, options.SpeechPath, Arg.Any<IProgress<DownloadProgress>?>(), Arg.Any<CancellationToken>());
        Assert.Equal(ProvisioningState.Idle, second);
    }

    [Fact]
    public async Task Reintentar_no_borra_la_descarga_parcial()
    {
        var options = Options();
        var partial = options.SpeechPath + ".part";
        File.WriteAllBytes(partial, [1, 2, 3]);

        source
            .FetchSpeechAsync(options.SpeechFileName, options.SpeechPath, Arg.Any<IProgress<DownloadProgress>?>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new IOException("se cortó"));

        await Build(options).ProvisionAsync(progress: null);

        // ModelProvisioner never touches the .part file itself — resuming, or
        // deleting it on an unrecoverable failure, is IModelSource's job. This
        // guards against that responsibility drifting up by accident.
        Assert.True(File.Exists(partial));
    }

    /// <summary>
    /// A synchronous stand-in for <see cref="Progress{T}"/>. The real type posts
    /// through the ambient <see cref="SynchronizationContext"/>, which makes it
    /// unusable for assertions immediately after an awaited call in a test.
    /// </summary>
    private sealed class RecordingProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

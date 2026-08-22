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

    /// <summary>
    /// The correction leg is optional (nullable), so most tests build without
    /// it. Only the tests that exercise the third leg opt in via this helper.
    /// </summary>
    private ProvisioningOptions OptionsWithCorrection(bool hasGpu = true) => Options() with
    {
        CorrectionFileName = "qwen2.5-3b-instruct-q4_k_m.gguf",
        CorrectionUrl = "https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/main/qwen2.5-3b-instruct-q4_k_m.gguf",
        CorrectionLabel = "qwen2.5-3b-instruct",
        CorrectionSize = "~2 GB",
        HasGpu = hasGpu,
    };

    private static void StubSpeechAndVad(IModelSource source, ProvisioningOptions options)
    {
        source
            .FetchSpeechAsync(options.SpeechFileName, options.SpeechPath, Arg.Any<IProgress<DownloadProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { File.WriteAllBytes(options.SpeechPath, []); return Task.CompletedTask; });

        source
            .FetchVadAsync(options.VadPath, Arg.Any<CancellationToken>())
            .Returns(_ => { File.WriteAllBytes(options.VadPath, []); return Task.CompletedTask; });
    }

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

    [Fact]
    public void NeedsProvisioning_es_true_para_un_usuario_que_actualiza_desde_Ollama()
    {
        // Speech + VAD already on disk (this user is not on a first run) but the
        // correction GGUF is configured and missing: exactly the population this
        // whole phase exists to catch. NeedsProvisioning is file-existence-based,
        // not IsFirstRun-based, precisely so this case trips provisioning without
        // any dedicated migration-detection code — a NeedsProvisioning that only
        // ever looked at Speech+VAD would silently never download the correction
        // model for anyone upgrading from an Ollama-era install.
        var options = OptionsWithCorrection();
        File.WriteAllBytes(options.SpeechPath, []);
        File.WriteAllBytes(options.VadPath, []);

        var provisioner = Build(options);

        Assert.True(provisioner.NeedsProvisioning);
    }

    [Fact]
    public void NeedsProvisioning_sigue_en_false_si_la_correccion_no_esta_configurada()
    {
        // Today's Phase-2-only Program.cs wiring (and any future machine where
        // correction is deliberately left off): CorrectionFileName null must not
        // force provisioning just because CorrectionPath is also null. This is
        // the null-safety half of the contract — without the explicit
        // CorrectionFileName check, a null CorrectionPath could be misread as
        // "missing" instead of "not configured".
        var options = Options();
        File.WriteAllBytes(options.SpeechPath, []);
        File.WriteAllBytes(options.VadPath, []);

        var provisioner = Build(options);

        Assert.False(provisioner.NeedsProvisioning);
    }

    [Fact]
    public void NeedsProvisioning_ignora_la_correccion_configurada_sin_GPU()
    {
        // Same HasGpu gate ProvisionAsync's own third leg already applies: a
        // CPU-only machine must never be told it "needs provisioning" for a
        // correction leg that ProvisionAsync itself would go on to skip anyway.
        // Without this, a CPU-only user with Speech+VAD already installed would
        // see the provisioning card flash open and closed for a download that
        // was never going to happen.
        var options = OptionsWithCorrection(hasGpu: false);
        File.WriteAllBytes(options.SpeechPath, []);
        File.WriteAllBytes(options.VadPath, []);

        var provisioner = Build(options);

        Assert.False(provisioner.NeedsProvisioning);
    }

    [Fact]
    public async Task Baja_el_modelo_de_correccion_como_tercera_etapa_despues_del_habla_y_el_detector()
    {
        var options = OptionsWithCorrection();
        StubSpeechAndVad(source, options);

        source
            .FetchAsync(options.CorrectionUrl!, options.CorrectionPath!, Arg.Any<IProgress<DownloadProgress>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { File.WriteAllBytes(options.CorrectionPath!, []); return Task.CompletedTask; });

        var reported = new List<ProvisioningState>();
        var progress = new RecordingProgress<ProvisioningStatus>(s => reported.Add(s.State));

        var result = await Build(options).ProvisionAsync(progress);

        Assert.Equal(ProvisioningState.Ready, result);
        Assert.Equal(
            [ProvisioningState.DownloadingSpeech, ProvisioningState.PreparingVad, ProvisioningState.DownloadingCorrection, ProvisioningState.Ready],
            reported);
        Assert.True(File.Exists(options.CorrectionPath));
    }

    [Fact]
    public async Task Un_error_al_bajar_la_correccion_no_impide_terminar_Ready()
    {
        // "Everything optional degrades to nothing": a failed 2 GB correction
        // download must not cost the user a working Whisper install.
        var options = OptionsWithCorrection();
        StubSpeechAndVad(source, options);

        source
            .FetchAsync(options.CorrectionUrl!, options.CorrectionPath!, Arg.Any<IProgress<DownloadProgress>?>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new IOException("sin conexión"));

        var reported = new List<ProvisioningState>();
        var progress = new RecordingProgress<ProvisioningStatus>(s => reported.Add(s.State));

        var result = await Build(options).ProvisionAsync(progress);

        Assert.Equal(ProvisioningState.Ready, result);
        Assert.DoesNotContain(ProvisioningState.Failed, reported);
        Assert.False(File.Exists(options.CorrectionPath));
    }

    [Fact]
    public async Task Sin_GPU_no_se_intenta_bajar_el_modelo_de_correccion()
    {
        // Same role HardwareProbe already plays for the Whisper model: a 3B
        // model can never land inside the 2s budget on CPU, so sparing the
        // user a ~2 GB download for a feature that can never work there.
        var options = OptionsWithCorrection(hasGpu: false);
        StubSpeechAndVad(source, options);

        var result = await Build(options).ProvisionAsync(progress: null);

        Assert.Equal(ProvisioningState.Ready, result);
        await source.DidNotReceive().FetchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<DownloadProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sin_correccion_configurada_no_se_intenta_bajar_nada_de_mas()
    {
        // The third leg is optional: ProvisioningOptions built without the
        // correction fields (today's production shape, until Phase 3 wires
        // real GGUF coordinates) must behave exactly like before this change.
        var options = Options();
        StubSpeechAndVad(source, options);

        var reported = new List<ProvisioningState>();
        var progress = new RecordingProgress<ProvisioningStatus>(s => reported.Add(s.State));

        var result = await Build(options).ProvisionAsync(progress);

        Assert.Equal(ProvisioningState.Ready, result);
        Assert.DoesNotContain(ProvisioningState.DownloadingCorrection, reported);
        await source.DidNotReceive().FetchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<DownloadProgress>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancelar_durante_la_descarga_de_la_correccion_no_se_reporta_como_error()
    {
        // Same "our own token" filter as the speech leg: quitting from the
        // tray mid-GGUF-download is not a failure, and must not be swallowed
        // by the correction leg's own non-fatal catch either.
        var options = OptionsWithCorrection();
        StubSpeechAndVad(source, options);

        using var shutdown = new CancellationTokenSource();
        await shutdown.CancelAsync();

        source
            .FetchAsync(options.CorrectionUrl!, options.CorrectionPath!, Arg.Any<IProgress<DownloadProgress>?>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new OperationCanceledException(shutdown.Token));

        var reported = new List<ProvisioningState>();
        var progress = new RecordingProgress<ProvisioningStatus>(s => reported.Add(s.State));

        var result = await Build(options).ProvisionAsync(progress, shutdown.Token);

        Assert.Equal(ProvisioningState.Idle, result);
        Assert.DoesNotContain(ProvisioningState.Failed, reported);
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

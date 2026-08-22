using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Otto.Core;

namespace Otto.Core.Tests;

/// <summary>
/// The whole dictation flow, exercised without a microphone, a GPU or a foreground
/// window. That this is possible at all is the payoff of keeping every
/// platform-specific concern behind a port.
/// </summary>
public class DictationPipelineTests
{
    private readonly IHotkeyService hotkey = Substitute.For<IHotkeyService>();
    private readonly IAudioCapture capture = Substitute.For<IAudioCapture>();
    private readonly ITranscriber transcriber = Substitute.For<ITranscriber>();
    private readonly ITextInjector injector = Substitute.For<ITextInjector>();
    private readonly IForegroundWindow foreground = Substitute.For<IForegroundWindow>();
    private readonly INoteRepository notes = Substitute.For<INoteRepository>();

    // Passes the text through untouched, which is exactly what Otto does when no
    // local model is installed — the configuration most people will run.
    private readonly IPostProcessor postProcessor = new NullPostProcessor();

    private DictationPipeline Build(IPostProcessor? postProcessor = null)
    {
        foreground.Current().Returns(new DictationContext("code", "Program.cs"));
        capture.Stop().Returns(new AudioBuffer(new float[16_000]));

        notes
            .AddAsync(Arg.Any<string>(), Arg.Any<DictationContext>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call => new Note(
                1, "", call.ArgAt<string>(0), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                call.ArgAt<DictationContext>(1), call.ArgAt<TimeSpan>(2)));

        return new DictationPipeline(
            hotkey, capture, transcriber, injector, foreground, notes, postProcessor ?? this.postProcessor,
            NullLogger<DictationPipeline>.Instance);
    }

    private async Task<DictationPipeline> StartedAsync()
    {
        var pipeline = Build();
        await pipeline.StartAsync(HotkeyBinding.Default);
        return pipeline;
    }

    [Fact]
    public async Task Carga_el_modelo_una_sola_vez_al_arrancar()
    {
        using var pipeline = await StartedAsync();

        await transcriber.Received(1).LoadAsync(Arg.Any<CancellationToken>());
        Assert.Equal(DictationState.Idle, pipeline.State);
    }

    [Fact]
    public async Task RegisteredHotkey_se_completa_recien_despues_de_un_registro_exitoso()
    {
        using var pipeline = await StartedAsync();

        Assert.Equal(HotkeyBinding.Default, pipeline.RegisteredHotkey);
    }

    [Fact]
    public async Task Un_registro_que_falla_deja_RegisteredHotkey_en_null_y_propaga_la_excepcion()
    {
        // StartAsync deliberately has no try/catch around hotkey.Register: this one
        // failure has to reach a layer with a window to open, and that is Otto.App,
        // not the pipeline, which keeps swallowing everything else by design.
        var failure = new HotkeyRegistrationException(HotkeyBinding.Default, alreadyInUse: true);
        hotkey.When(h => h.Register(Arg.Any<HotkeyBinding>())).Do(_ => throw failure);

        using var pipeline = Build();

        await Assert.ThrowsAsync<HotkeyRegistrationException>(() => pipeline.StartAsync(HotkeyBinding.Default));

        Assert.Null(pipeline.RegisteredHotkey);
        Assert.Equal(DictationState.Loading, pipeline.State);
    }

    [Fact]
    public async Task Un_dictado_completo_transcribe_e_inyecta()
    {
        transcriber
            .TranscribeAsync(Arg.Any<AudioBuffer>(), Arg.Any<DictationContext>(), Arg.Any<CancellationToken>())
            .Returns("hola mundo");

        using var pipeline = await StartedAsync();

        await RunDictationAsync(pipeline);

        capture.Received(1).Start();
        capture.Received(1).Stop();
        await injector.Received(1).InjectAsync("hola mundo", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task El_contexto_se_toma_al_apretar_y_no_al_soltar()
    {
        // The user may switch windows while speaking. What matters is where they
        // were when they started, because that is what the prompt has to match.
        var atPress = new DictationContext("code", "Program.cs");
        foreground.Current().Returns(atPress, new DictationContext("chrome", "otra cosa"));

        transcriber
            .TranscribeAsync(Arg.Any<AudioBuffer>(), Arg.Any<DictationContext>(), Arg.Any<CancellationToken>())
            .Returns("texto");

        using var pipeline = await StartedAsync();
        await RunDictationAsync(pipeline);

        await transcriber.Received(1).TranscribeAsync(
            Arg.Any<AudioBuffer>(), Arg.Is<DictationContext>(c => c.ProcessName == "code"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Una_transcripcion_vacia_no_inyecta_nada()
    {
        // Silence must be silent. Injecting an empty string would still fire a
        // paste into the user's document.
        transcriber
            .TranscribeAsync(Arg.Any<AudioBuffer>(), Arg.Any<DictationContext>(), Arg.Any<CancellationToken>())
            .Returns("   ");

        using var pipeline = await StartedAsync();
        await RunDictationAsync(pipeline);

        await injector.DidNotReceive().InjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Si_falla_la_transcripcion_vuelve_a_estar_listo()
    {
        // A background dictation tool that dies on one bad transcription leaves the
        // user with nothing running and no explanation.
        transcriber
            .TranscribeAsync(Arg.Any<AudioBuffer>(), Arg.Any<DictationContext>(), Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new InvalidOperationException("boom"));

        using var pipeline = await StartedAsync();
        await RunDictationAsync(pipeline);

        Assert.Equal(DictationState.Idle, pipeline.State);
    }

    [Fact]
    public async Task Guarda_la_nota_despues_de_inyectar_y_no_antes()
    {
        // The user's latency budget is spent by the time the text appears. A disk
        // write must not be able to add to it.
        var order = new List<string>();

        transcriber
            .TranscribeAsync(Arg.Any<AudioBuffer>(), Arg.Any<DictationContext>(), Arg.Any<CancellationToken>())
            .Returns("hola");

        injector
            .When(i => i.InjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("inyectar"));

        notes
            .When(n => n.AddAsync(Arg.Any<string>(), Arg.Any<DictationContext>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()))
            .Do(_ => order.Add("guardar"));

        using var pipeline = await StartedAsync();
        await RunDictationAsync(pipeline);

        for (var attempt = 0; attempt < 100 && order.Count < 2; attempt++)
            await Task.Delay(10);

        Assert.Equal(["inyectar", "guardar"], order);
    }

    [Fact]
    public async Task Si_falla_el_guardado_el_dictado_igual_se_escribio()
    {
        transcriber
            .TranscribeAsync(Arg.Any<AudioBuffer>(), Arg.Any<DictationContext>(), Arg.Any<CancellationToken>())
            .Returns("hola");

        notes
            .AddAsync(Arg.Any<string>(), Arg.Any<DictationContext>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<Task<Note>>(_ => throw new IOException("disco lleno"));

        using var pipeline = await StartedAsync();
        await RunDictationAsync(pipeline);

        await injector.Received(1).InjectAsync("hola", Arg.Any<CancellationToken>());
        Assert.Equal(DictationState.Idle, pipeline.State);
    }

    [Fact]
    public async Task StartAsync_no_espera_a_que_termine_de_cargar_el_corrector()
    {
        // Design decision #4: transcriber.LoadAsync → hotkey registered → Idle →
        // the corrector loads in the background. Awaiting the corrector's own
        // ProbeAsync here would add its full load time to every launch of an
        // autostart tray app; dictation must already be usable on raw Whisper
        // output the moment StartAsync returns.
        var releaseProbe = new TaskCompletionSource();
        var postProcessor = Substitute.For<IPostProcessor>();
        postProcessor.Enabled.Returns(true);
        postProcessor.ProbeAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await releaseProbe.Task;
            return true;
        });

        using var pipeline = Build(postProcessor);

        // Would hang here forever if StartAsync awaited ProbeAsync directly —
        // this await returning at all is the assertion.
        await pipeline.StartAsync(HotkeyBinding.Default);

        Assert.Equal(DictationState.Idle, pipeline.State);

        releaseProbe.SetResult();
    }

    [Fact]
    public async Task CorrectionAvailabilityChanged_se_dispara_cuando_termina_la_carga_diferida()
    {
        var releaseProbe = new TaskCompletionSource();
        var postProcessor = Substitute.For<IPostProcessor>();
        postProcessor.Enabled.Returns(true);
        postProcessor.ProbeAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await releaseProbe.Task;
            return true;
        });

        using var pipeline = Build(postProcessor);
        var fired = false;
        pipeline.CorrectionAvailabilityChanged += () => fired = true;

        await pipeline.StartAsync(HotkeyBinding.Default);

        // Not yet: the deferred load has not settled, so the event must not have
        // fired just because Idle was reached.
        Assert.False(fired);

        releaseProbe.SetResult();

        for (var attempt = 0; attempt < 100 && !fired; attempt++)
            await Task.Delay(10);

        Assert.True(fired);
        await postProcessor.Received(1).ProbeAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The exact fix for a startup trap: IPostProcessor is now ALWAYS the real
    /// LlamaPostProcessor on GPU hardware, regardless of Settings.CorrectVoseo
    /// — otherwise the correction toggle could never be turned on at runtime
    /// after starting off. That decoupling only works if StartAsync itself
    /// never loads a ~2 GB model into VRAM for a feature the user has
    /// switched off — Enabled is the live signal for that, read fresh at
    /// startup rather than baked into which IPostProcessor got constructed.
    /// </summary>
    [Fact]
    public async Task La_carga_diferida_no_arranca_si_el_post_processor_esta_deshabilitado()
    {
        var postProcessor = Substitute.For<IPostProcessor>();
        postProcessor.Enabled.Returns(false);

        using var pipeline = Build(postProcessor);

        await pipeline.StartAsync(HotkeyBinding.Default);

        // A little slack for the fire-and-forget LoadCorrectorAsync to run —
        // it should find nothing to do and return immediately either way.
        await Task.Delay(30);

        await postProcessor.DidNotReceive().ProbeAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Dispose_libera_el_post_processor_si_implementa_IDisposable()
    {
        // LlamaPostProcessor owns a LlamaEngine, and that engine's native
        // GGUF/Vulkan handles were never released anywhere in production
        // before this test existed — Program.cs builds it inline in the
        // IPostProcessor factory, never registers it as its own DI service,
        // and DictationPipeline.Dispose() disposed only hotkey/capture.
        var disposablePostProcessor = Substitute.For<IPostProcessor, IDisposable>();
        var pipeline = Build((IPostProcessor)disposablePostProcessor);

        pipeline.Dispose();

        ((IDisposable)disposablePostProcessor).Received(1).Dispose();
    }

    [Fact]
    public void Dispose_no_falla_si_el_post_processor_no_implementa_IDisposable()
    {
        // NullPostProcessor — what most installs run with, or CPU-only
        // hardware — has nothing to release. Dispose() must not assume
        // every IPostProcessor is also an IDisposable.
        var pipeline = Build(new NullPostProcessor());

        var exception = Record.Exception(() => pipeline.Dispose());

        Assert.Null(exception);
    }

    [Fact]
    public async Task Soltar_sin_haber_apretado_no_hace_nada()
    {
        using var pipeline = await StartedAsync();

        hotkey.Released += Raise.Event<Action>();

        capture.DidNotReceive().Stop();
        Assert.Equal(DictationState.Idle, pipeline.State);
    }

    /// <summary>
    /// Press, release, and wait for the transcription that release kicks off — the
    /// pipeline deliberately does not await it, so the hotkey callback never blocks
    /// the message loop.
    /// </summary>
    private async Task RunDictationAsync(DictationPipeline pipeline)
    {
        hotkey.Pressed += Raise.Event<Action>();
        hotkey.Released += Raise.Event<Action>();

        for (var attempt = 0; attempt < 100 && pipeline.State != DictationState.Idle; attempt++)
            await Task.Delay(10);
    }
}

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

    private DictationPipeline Build()
    {
        foreground.Current().Returns(new DictationContext("code", "Program.cs"));
        capture.Stop().Returns(new AudioBuffer(new float[16_000]));

        notes
            .AddAsync(Arg.Any<string>(), Arg.Any<DictationContext>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call => new Note(
                1, "", call.ArgAt<string>(0), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                call.ArgAt<DictationContext>(1), call.ArgAt<TimeSpan>(2)));

        return new DictationPipeline(
            hotkey, capture, transcriber, injector, foreground, notes, NullLogger<DictationPipeline>.Instance);
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

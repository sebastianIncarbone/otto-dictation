using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Otto.Core;
using Otto.PostProcessing;

namespace Otto.Core.Tests;

/// <summary>
/// <see cref="LlamaPostProcessor"/> against a mocked <see cref="ICorrectionEngine"/> —
/// no GGUF, no GPU. This is the seam the deleted Ollama-era corrector never had,
/// which is why it shipped with zero tests.
/// </summary>
public class LlamaPostProcessorTests
{
    private static readonly DictationContext Context = new("code", "Program.cs");

    private readonly ICorrectionEngine engine = Substitute.For<ICorrectionEngine>();

    private LlamaPostProcessor Build(TimeSpan? timeout = null, TimeSpan? probeTimeout = null) =>
        new(engine, new PostProcessingOptions
        {
            ModelPath = "unused.gguf",
            Timeout = timeout ?? TimeSpan.FromMilliseconds(200),
            ProbeTimeout = probeTimeout ?? TimeSpan.FromSeconds(60),
        }, NullLogger<LlamaPostProcessor>.Instance);

    /// <summary>Loads and warms the processor up, then clears the call log so each
    /// test only sees the calls it makes itself.</summary>
    private async Task<LlamaPostProcessor> ProbedAsync(TimeSpan? timeout = null)
    {
        engine.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns("Hola.");

        var processor = Build(timeout);
        await processor.ProbeAsync();

        engine.ClearReceivedCalls();
        return processor;
    }

    [Fact]
    public async Task Corrige_el_texto_dentro_del_presupuesto()
    {
        var processor = await ProbedAsync();

        const string original = "Che, ¿me puedes revisar el pull request que subí recién?";
        const string corrected = "Che, ¿me podés revisar el pull request que subí recién?";

        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(corrected);

        var result = await processor.ProcessAsync(original, Context);

        Assert.Equal(corrected, result);
        await engine.Received(1).ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Descarta_una_correccion_que_EditGuard_rechaza()
    {
        var processor = await ProbedAsync();

        const string original = "Che, ¿me puedes revisar el pull request?";

        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns("¡Claro! Veo que te has olvidado de añadir el voseo al verbo revisa. " +
                     "Aquí está el texto corregido: Che, ¿me podés revisar el pull request?");

        var result = await processor.ProcessAsync(original, Context);

        Assert.Equal(original, result);
    }

    [Fact]
    public async Task Vuelve_al_texto_crudo_cuando_la_inferencia_supera_el_presupuesto()
    {
        var processor = await ProbedAsync(TimeSpan.FromMilliseconds(30));

        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => EsperarCancelacionAsync(callInfo.ArgAt<CancellationToken>(1)));

        const string original = "Che, ¿me puedes revisar el pull request?";
        var result = await processor.ProcessAsync(original, Context);

        Assert.Equal(original, result);

        static async Task<string> EsperarCancelacionAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return "no debería llegar acá";
        }
    }

    [Fact]
    public async Task Vuelve_al_texto_crudo_si_el_motor_lanza_una_excepcion()
    {
        var processor = await ProbedAsync();

        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string>(new InvalidOperationException("boom")));

        const string original = "Che, ¿me puedes revisar el pull request?";
        var result = await processor.ProcessAsync(original, Context);

        Assert.Equal(original, result);
    }

    [Fact]
    public async Task Vuelve_al_texto_crudo_si_el_modelo_no_esta_disponible()
    {
        // Never probed: IsAvailable stays en falso, como cualquier instalación sin
        // GPU compatible o con el GGUF todavía sin descargar.
        var processor = Build();

        const string original = "Che, ¿me puedes revisar el pull request?";
        var result = await processor.ProcessAsync(original, Context);

        Assert.Equal(original, result);
        await engine.DidNotReceive().ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsAvailable_queda_en_falso_si_el_modelo_no_carga()
    {
        engine.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("no vulkan-1.dll")));

        var processor = Build();
        var available = await processor.ProbeAsync();

        Assert.False(available);
        Assert.False(processor.IsAvailable);
    }

    [Fact]
    public async Task Vuelve_al_texto_crudo_cuando_el_dictado_excede_el_contexto()
    {
        var processor = await ProbedAsync();

        // ~5000 caracteres ≈ ~1667 tokens estimados, por encima del cupo de 1024
        // que deja lugar al prompt fijo y a la salida dentro del ContextSize.
        var extenso = string.Concat(Enumerable.Repeat("hola mundo ", 500));

        var result = await processor.ProcessAsync(extenso, Context);

        Assert.Equal(extenso, result);
        await engine.DidNotReceive().ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsAvailable_no_queda_en_true_mientras_el_warmup_todavia_corre()
    {
        // The race this pins down: ProbeAsync used to flip IsAvailable to true
        // right after LoadAsync, BEFORE WarmUpAsync's own ChatAsync("Hola.") had
        // returned. ProcessAsync's only gate is IsAvailable, so a dictation
        // landing in that exact window would call ChatAsync a second time
        // concurrently against the SAME non-reentrant InteractiveExecutor/
        // LLamaContext the warm-up call was still using. This test proves the
        // fix deterministically rather than by racing threads: while the warm-up
        // call is still in flight, IsAvailable must read false, so ProcessAsync
        // returns the raw text without ever calling ChatAsync a second time.
        var warmupStarted = new TaskCompletionSource();
        var releaseWarmup = new TaskCompletionSource();

        engine.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var messages = callInfo.ArgAt<IReadOnlyList<(string Role, string Content)>>(0);

                if (messages[^1].Content == "Hola.")
                {
                    warmupStarted.SetResult();
                    await releaseWarmup.Task;
                }

                return "Hola.";
            });

        var processor = Build();
        var probe = processor.ProbeAsync();

        await warmupStarted.Task;

        Assert.False(processor.IsAvailable);

        const string original = "che, como andas";
        var result = await processor.ProcessAsync(original, Context);

        Assert.Equal(original, result);
        await engine.Received(1).ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>());

        releaseWarmup.SetResult();
        await probe;

        Assert.True(processor.IsAvailable);
    }

    [Fact]
    public async Task Un_segundo_ProbeAsync_no_repite_la_carga_si_el_primero_tuvo_exito()
    {
        engine.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns("Hola.");

        var processor = Build();

        var primero = await processor.ProbeAsync();
        var segundo = await processor.ProbeAsync();

        Assert.True(primero);
        Assert.True(segundo);
        await engine.Received(1).LoadAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Despues_de_una_carga_fallida_un_nuevo_ProbeAsync_reintenta_y_puede_tener_exito()
    {
        // Este es el bug crítico: antes de la corrección, ProbeAsync marcaba el
        // intento como "hecho" pasara lo que pasara, así que el reintento del
        // tray después de una carga fallida era un no-op permanente — nunca
        // volvía a llamar a engine.LoadAsync.
        engine.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("no vulkan-1.dll")));

        var processor = Build();

        var primero = await processor.ProbeAsync();

        Assert.False(primero);
        Assert.False(processor.IsAvailable);

        // El clic de "reintentar" en el tray llama a ProbeAsync de nuevo sobre
        // la MISMA instancia — esta vez la carga sí puede tener éxito.
        engine.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns("Hola.");

        var segundo = await processor.ProbeAsync();

        Assert.True(segundo);
        Assert.True(processor.IsAvailable);
        await engine.Received(2).LoadAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Despues_de_un_timeout_en_la_carga_un_nuevo_ProbeAsync_reintenta()
    {
        // Mismo bug que el test anterior, pero por la otra vía de falla: un
        // ProbeTimeout vencido (carga colgada, driver de Vulkan trabado) en vez
        // de una excepción directa del motor.
        engine.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.Delay(Timeout.Infinite, callInfo.ArgAt<CancellationToken>(0)));

        var processor = Build(probeTimeout: TimeSpan.FromMilliseconds(30));

        var primero = await processor.ProbeAsync();

        Assert.False(primero);
        Assert.False(processor.IsAvailable);

        engine.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns("Hola.");

        var segundo = await processor.ProbeAsync();

        Assert.True(segundo);
        Assert.True(processor.IsAvailable);
        await engine.Received(2).LoadAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Dispose_libera_el_motor_si_implementa_IDisposable()
    {
        // Production LlamaEngine implements both ICorrectionEngine and
        // IDisposable; the fake used everywhere else in this file only
        // implements the former, so this test needs its own substitute.
        var disposableEngine = Substitute.For<ICorrectionEngine, IDisposable>();
        var processor = new LlamaPostProcessor(
            (ICorrectionEngine)disposableEngine,
            new PostProcessingOptions { ModelPath = "unused.gguf" },
            NullLogger<LlamaPostProcessor>.Instance);

        processor.Dispose();

        ((IDisposable)disposableEngine).Received(1).Dispose();
    }

    [Fact]
    public void Dispose_no_falla_si_el_motor_no_implementa_IDisposable()
    {
        // The engine field used by every other test in this file is a plain
        // ICorrectionEngine substitute, with no IDisposable — Dispose() must
        // not assume every engine is one.
        var processor = Build();

        var exception = Record.Exception(() => processor.Dispose());

        Assert.Null(exception);
    }

    [Fact]
    public async Task ProbeAsync_concurrente_carga_el_motor_una_sola_vez()
    {
        var loadStarted = new TaskCompletionSource();
        var releaseLoad = new TaskCompletionSource();

        engine.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                loadStarted.SetResult();
                await releaseLoad.Task;
            });
        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns("Hola.");

        var processor = Build();

        var first = processor.ProbeAsync();
        await loadStarted.Task;
        var second = processor.ProbeAsync();

        releaseLoad.SetResult();
        await Task.WhenAll(first, second);

        await engine.Received(1).LoadAsync(Arg.Any<CancellationToken>());
        Assert.True(processor.IsAvailable);
    }
}

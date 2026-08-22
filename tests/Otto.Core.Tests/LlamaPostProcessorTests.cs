using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Otto.Core;
using Otto.PostProcessing;

namespace Otto.Core.Tests;

/// <summary>
/// <see cref="LlamaPostProcessor"/> against a mocked <see cref="ICorrectionEngine"/> —
/// no GGUF, no GPU. This is the seam <see cref="OllamaPostProcessor"/> never had,
/// which is why it shipped with zero tests.
/// </summary>
public class LlamaPostProcessorTests
{
    private static readonly DictationContext Context = new("code", "Program.cs");

    private readonly ICorrectionEngine engine = Substitute.For<ICorrectionEngine>();

    private LlamaPostProcessor Build(TimeSpan? timeout = null) =>
        new(engine, new PostProcessingOptions
        {
            ModelPath = "unused.gguf",
            Timeout = timeout ?? TimeSpan.FromMilliseconds(200),
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

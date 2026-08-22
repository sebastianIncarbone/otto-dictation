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

    private LlamaPostProcessor Build(
        TimeSpan? timeout = null,
        TimeSpan? probeTimeout = null,
        bool enabled = true,
        TimeSpan? idleTimeout = null,
        TimeProvider? clock = null) =>
        new(engine, new PostProcessingOptions
        {
            ModelPath = "unused.gguf",
            Timeout = timeout ?? TimeSpan.FromMilliseconds(200),
            ProbeTimeout = probeTimeout ?? TimeSpan.FromSeconds(60),
            IdleUnloadInterval = idleTimeout,
        }, NullLogger<LlamaPostProcessor>.Instance, enabled, clock);

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

    // ---- Runtime enable/disable — the Settings checkbox and the tray toggle
    // both go through SetEnabledAsync, mirroring the Ready/Missing/Failed
    // recovery ProbeAsync already offers, but now reversible in both
    // directions on the SAME processor instance.

    [Fact]
    public async Task SetEnabledAsync_true_carga_el_modelo_si_no_estaba_disponible()
    {
        engine.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns("Hola.");

        var processor = Build(enabled: false);
        Assert.False(processor.Enabled);

        await processor.SetEnabledAsync(true);

        Assert.True(processor.Enabled);
        Assert.True(processor.IsAvailable);
        await engine.Received(1).LoadAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetEnabledAsync_false_descarga_el_modelo_ya_cargado()
    {
        engine.UnloadAsync(Arg.Any<CancellationToken>()).Returns(true);

        var processor = await ProbedAsync();
        Assert.True(processor.IsAvailable);

        await processor.SetEnabledAsync(false);

        Assert.False(processor.Enabled);
        Assert.False(processor.IsAvailable);
        await engine.Received(1).UnloadAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetEnabledAsync_con_el_mismo_valor_que_ya_tenia_no_toca_el_motor()
    {
        var processor = Build(); // enabled: true by default

        await processor.SetEnabledAsync(true);

        await engine.DidNotReceive().LoadAsync(Arg.Any<CancellationToken>());
        await engine.DidNotReceive().UnloadAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The engine gave up rather than freeing memory underneath a live call
    /// (InFlightGate.TryUnload's own bounded wait) — the processor must not
    /// report the model as gone when nothing was actually freed. Otherwise a
    /// later ProbeAsync would start a SECOND load racing the still-resident
    /// weights from the first one.
    /// </summary>
    [Fact]
    public async Task UnloadAsync_que_el_motor_no_logra_liberar_deja_el_modelo_disponible()
    {
        engine.UnloadAsync(Arg.Any<CancellationToken>()).Returns(false);

        var processor = await ProbedAsync();

        await processor.UnloadAsync();

        Assert.True(processor.IsAvailable);
    }

    [Fact]
    public async Task UnloadAsync_sin_haber_cargado_nunca_no_llama_al_motor()
    {
        var processor = Build();

        await processor.UnloadAsync();

        await engine.DidNotReceive().UnloadAsync(Arg.Any<CancellationToken>());
    }

    // ---- Idle-unload timer — measured from the last correction (or the
    // load itself). On expiry the model unloads and the NEXT dictation that
    // needs it triggers a background reload rather than waiting on one.

    [Fact]
    public async Task El_temporizador_de_inactividad_descarga_el_modelo_al_vencer()
    {
        var clock = new FakeTimeProvider();

        engine.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns("Hola.");
        engine.UnloadAsync(Arg.Any<CancellationToken>()).Returns(true);

        var processor = Build(idleTimeout: TimeSpan.FromMinutes(15), clock: clock);
        await processor.ProbeAsync();
        Assert.True(processor.IsAvailable);

        clock.Advance(TimeSpan.FromMinutes(15));

        // The idle callback's UnloadAsync runs detached from Advance — poll
        // briefly rather than assuming it already settled synchronously.
        for (var attempt = 0; attempt < 100 && processor.IsAvailable; attempt++)
            await Task.Delay(10);

        Assert.False(processor.IsAvailable);
        await engine.Received(1).UnloadAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task El_temporizador_de_inactividad_no_descarga_antes_de_vencer()
    {
        var clock = new FakeTimeProvider();

        engine.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns("Hola.");

        var processor = Build(idleTimeout: TimeSpan.FromMinutes(15), clock: clock);
        await processor.ProbeAsync();

        clock.Advance(TimeSpan.FromMinutes(14));
        await Task.Delay(30); // give any (incorrectly) fired background unload a chance to run

        Assert.True(processor.IsAvailable);
        await engine.DidNotReceive().UnloadAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A correction counts as activity: it has to push the idle deadline out
    /// again, or a machine dictating every few minutes would still lose the
    /// model between two of them.
    /// </summary>
    [Fact]
    public async Task Una_correccion_reinicia_el_reloj_de_inactividad()
    {
        var clock = new FakeTimeProvider();

        engine.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns("Hola.");

        var processor = Build(idleTimeout: TimeSpan.FromMinutes(15), clock: clock);
        await processor.ProbeAsync();

        clock.Advance(TimeSpan.FromMinutes(10));

        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns("che, como andás");
        await processor.ProcessAsync("che, como andas", Context); // activity — resets the clock

        clock.Advance(TimeSpan.FromMinutes(10)); // 20 min since load, only 10 since the correction
        await Task.Delay(30);

        Assert.True(processor.IsAvailable);
    }

    [Fact]
    public async Task Despues_de_descargar_por_inactividad_ProcessAsync_dispara_una_recarga_en_segundo_plano()
    {
        var clock = new FakeTimeProvider();

        engine.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns("Hola.");
        engine.UnloadAsync(Arg.Any<CancellationToken>()).Returns(true);

        var processor = Build(idleTimeout: TimeSpan.FromMinutes(15), clock: clock);
        await processor.ProbeAsync();

        clock.Advance(TimeSpan.FromMinutes(15));

        for (var attempt = 0; attempt < 100 && processor.IsAvailable; attempt++)
            await Task.Delay(10);

        Assert.False(processor.IsAvailable);
        engine.ClearReceivedCalls();

        var result = await processor.ProcessAsync("che, como andas", Context);

        // This dictation itself still degrades to raw text — a reload must
        // never make a dictation wait on it.
        Assert.Equal("che, como andas", result);

        for (var attempt = 0; attempt < 100 && !processor.IsAvailable; attempt++)
            await Task.Delay(10);

        Assert.True(processor.IsAvailable);
        await engine.Received(1).LoadAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Dispose is terminal for the whole processor, not just the engine — a
    /// pending idle timer firing after Dispose would call UnloadAsync on an
    /// engine that may already be gone. Otto only ever calls Dispose right
    /// before process shutdown, so this is hygiene more than a real bug, but
    /// it is cheap to guarantee outright.
    /// </summary>
    [Fact]
    public async Task Dispose_detiene_el_temporizador_de_inactividad_pendiente()
    {
        var clock = new FakeTimeProvider();

        engine.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns("Hola.");

        var processor = Build(idleTimeout: TimeSpan.FromMinutes(15), clock: clock);
        await processor.ProbeAsync();

        processor.Dispose();

        clock.Advance(TimeSpan.FromMinutes(15));
        await Task.Delay(30);

        await engine.DidNotReceive().UnloadAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A model that has NEVER successfully loaded (missing GGUF, unsupported
    /// driver) must not get a fresh ProbeAsync attempt behind every single
    /// dictation — that would turn a per-dictation call into the exact "health
    /// check on the hot path" DictationPipeline's own design already rejects.
    /// This is the existing regression test above
    /// (Vuelve_al_texto_crudo_si_el_modelo_no_esta_disponible) restated as an
    /// explicit negative for the NEW reload trigger, so a future change to
    /// this file cannot quietly widen it.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_no_reintenta_un_modelo_que_nunca_llego_a_cargar()
    {
        var processor = Build(); // never probed

        await processor.ProcessAsync("che, como andas", Context);

        await engine.DidNotReceive().LoadAsync(Arg.Any<CancellationToken>());
    }

    // ---- Concurrent enable/disable — the race the fast paths outside `gate`
    // in ProbeAsync/UnloadAsync used to open. Every test below uses a
    // TaskCompletionSource to pin an exact interleaving deterministically
    // rather than racing real threads.

    [Fact]
    public async Task ProcessAsync_no_corrige_si_Enabled_es_falso_aunque_IsAvailable_siga_en_true()
    {
        // Requirement: ProcessAsync must respect Enabled, not just
        // IsAvailable — a disabled corrector must never correct, whatever
        // IsAvailable happens to say. Reached here without needing a real
        // race: a disable whose UnloadAsync gives up (engine.UnloadAsync
        // returns false, mirroring InFlightGate.TryUnload's own bounded-wait
        // failure) leaves IsAvailable true on purpose — see
        // UnloadAsync_que_el_motor_no_logra_liberar_deja_el_modelo_disponible
        // above — which is exactly the state this test needs.
        engine.UnloadAsync(Arg.Any<CancellationToken>()).Returns(false);

        var processor = await ProbedAsync();
        await processor.SetEnabledAsync(false);

        Assert.False(processor.Enabled);
        Assert.True(processor.IsAvailable); // the engine refused to free it

        var result = await processor.ProcessAsync("che, como andas", Context);

        Assert.Equal("che, como andas", result);
        await engine.DidNotReceive().ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Finding 1, repro A: a disable landing while <see cref="LlamaPostProcessor.ProbeAsync"/>
    /// is still inside <c>engine.LoadAsync</c>/<c>WarmUpAsync</c> used to
    /// return immediately from <see cref="LlamaPostProcessor.UnloadAsync"/>'s
    /// own un-gated fast path (<c>loadSucceeded</c> was still false at that
    /// exact instant), never waiting for or touching the gate the in-flight
    /// load held. The probe then finished normally and left the model
    /// resident — and correcting — with <see cref="LlamaPostProcessor.Enabled"/>
    /// already false. Fixed by removing that fast path: the disable now
    /// blocks on the same gate until the in-flight load settles, then frees
    /// the model it just loaded.
    /// </summary>
    [Fact]
    public async Task Deshabilitar_mientras_una_carga_esta_en_curso_no_deja_el_modelo_residente()
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
        engine.UnloadAsync(Arg.Any<CancellationToken>()).Returns(true);

        var processor = Build(enabled: true); // mirrors LoadCorrectorAsync's startup probe
        var probe = processor.ProbeAsync();

        await loadStarted.Task;
        Assert.False(processor.IsAvailable); // still mid-load

        var disable = processor.SetEnabledAsync(false);

        // Enabled flips synchronously the instant SetEnabledAsync(false) is
        // called — long before its own UnloadAsync ever gets a chance to
        // touch the gate the in-flight probe is holding.
        Assert.False(processor.Enabled);

        releaseLoad.SetResult();
        await probe; // the load ProbeAsync started before the disable finishes successfully
        await disable; // now the queued disable can finally run its unload

        Assert.False(processor.Enabled);
        Assert.False(processor.IsAvailable); // freed — not stranded resident
        await engine.Received(1).UnloadAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Finding 1, repro B: while a disable's own <see cref="LlamaPostProcessor.UnloadAsync"/>
    /// call was still inside <c>engine.UnloadAsync</c>'s own bounded wait
    /// (<c>loadSucceeded</c> still true at that instant), <see cref="LlamaPostProcessor.ProbeAsync"/>'s
    /// un-gated fast path used to see <c>loadSucceeded == true</c> and return
    /// as a no-op WITHOUT ever taking the gate — leaving <see cref="LlamaPostProcessor.Enabled"/>
    /// true but the model actually unloaded a moment later, stranded until a
    /// restart. Fixed by removing that fast path: every ProbeAsync call now
    /// takes the SAME gate UnloadAsync holds for its whole body, so a
    /// re-enable queued behind an in-flight unload correctly waits for it,
    /// then reloads.
    /// </summary>
    [Fact]
    public async Task Reactivar_mientras_el_motor_todavia_esta_descargando_recarga_en_vez_de_perder_el_modelo()
    {
        var unloadStarted = new TaskCompletionSource();
        var releaseUnload = new TaskCompletionSource<bool>();

        engine.UnloadAsync(Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                unloadStarted.SetResult();
                return await releaseUnload.Task;
            });

        var processor = await ProbedAsync();
        Assert.True(processor.IsAvailable);

        var disable = processor.SetEnabledAsync(false);
        await unloadStarted.Task;

        Assert.False(processor.Enabled); // flipped immediately; the unload is still running

        // Re-enable now, while the disable's UnloadAsync is still stuck
        // inside engine.UnloadAsync — this is exactly what used to get lost.
        engine.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns("Hola.");
        var enable = processor.SetEnabledAsync(true);

        Assert.True(processor.Enabled); // flipped immediately too

        releaseUnload.SetResult(true);
        await disable;
        await enable;

        Assert.True(processor.Enabled);
        Assert.True(processor.IsAvailable); // reloaded — not stranded
        await engine.Received(1).UnloadAsync(Arg.Any<CancellationToken>());
        await engine.Received(1).LoadAsync(Arg.Any<CancellationToken>()); // ProbedAsync already cleared the first load
    }

    /// <summary>
    /// The same interleaving as the repro-B test above, but reached through
    /// the idle timer's fire-and-forget <c>OnIdleExpired</c> path instead of
    /// <see cref="LlamaPostProcessor.SetEnabledAsync"/> — a genuinely
    /// different call shape (unawaited, triggered by wall-clock idle rather
    /// than a user action) worth pinning separately: a manual retry (the
    /// tray's "reintentar", or the background reload trigger) landing while
    /// an idle unload is still in flight must reload, not lose the model.
    /// </summary>
    [Fact]
    public async Task Un_ProbeAsync_manual_mientras_el_temporizador_de_inactividad_descarga_recarga_el_modelo()
    {
        var clock = new FakeTimeProvider();
        var unloadStarted = new TaskCompletionSource();
        var releaseUnload = new TaskCompletionSource<bool>();

        engine.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns("Hola.");
        engine.UnloadAsync(Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                unloadStarted.SetResult();
                return await releaseUnload.Task;
            });

        var processor = Build(idleTimeout: TimeSpan.FromMinutes(15), clock: clock);
        await processor.ProbeAsync();

        clock.Advance(TimeSpan.FromMinutes(15)); // fires OnIdleExpired's fire-and-forget UnloadAsync
        await unloadStarted.Task;

        var retry = processor.ProbeAsync();

        releaseUnload.SetResult(true);
        await retry;

        Assert.True(processor.IsAvailable);
        await engine.Received(1).UnloadAsync(Arg.Any<CancellationToken>());
        await engine.Received(2).LoadAsync(Arg.Any<CancellationToken>()); // startup load + the reload
    }

    /// <summary>
    /// Not a race — a plain sequential on→off→on chain, each step awaited in
    /// turn, triangulating the fix with the boring case: repeated toggling
    /// must converge without ever needing a restart to escape a stuck state.
    /// </summary>
    [Fact]
    public async Task Prender_apagar_y_volver_a_prender_en_secuencia_termina_con_el_modelo_cargado()
    {
        engine.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns("Hola.");
        engine.UnloadAsync(Arg.Any<CancellationToken>()).Returns(true);

        var processor = Build(enabled: false);

        await processor.SetEnabledAsync(true);
        Assert.True(processor.IsAvailable);

        await processor.SetEnabledAsync(false);
        Assert.False(processor.IsAvailable);

        await processor.SetEnabledAsync(true);

        Assert.True(processor.Enabled);
        Assert.True(processor.IsAvailable);
        await engine.Received(2).LoadAsync(Arg.Any<CancellationToken>());
        await engine.Received(1).UnloadAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Dispose is terminal and bypasses `gate` on purpose (see its own doc
    /// comment) specifically so shutdown never blocks behind a slow or hung
    /// load — this pins that down against the concurrency this fix touches,
    /// rather than assuming it.
    /// </summary>
    [Fact]
    public async Task Dispose_mientras_una_carga_esta_en_curso_no_cuelga_ni_lanza()
    {
        var disposableEngine = Substitute.For<ICorrectionEngine, IDisposable>();
        var loadStarted = new TaskCompletionSource();
        var releaseLoad = new TaskCompletionSource();

        ((ICorrectionEngine)disposableEngine).LoadAsync(Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                loadStarted.SetResult();
                await releaseLoad.Task;
            });

        var processor = new LlamaPostProcessor(
            (ICorrectionEngine)disposableEngine,
            new PostProcessingOptions { ModelPath = "unused.gguf" },
            NullLogger<LlamaPostProcessor>.Instance);

        var probe = processor.ProbeAsync();
        await loadStarted.Task;

        var exception = Record.Exception(() => processor.Dispose());

        Assert.Null(exception);
        ((IDisposable)disposableEngine).Received(1).Dispose();

        // Let the orphaned probe unwind so it does not outlive the test.
        releaseLoad.SetResult();
        await probe;
    }

    // ---- AvailabilityChanged / IdleUnloaded — Finding 1 (round 2 review): every
    // transition that changes what the tray should show — a load settling, an
    // idle unload, the background reload that follows it, a manual toggle —
    // must be observable, not just the one-time startup load. The "did this
    // actually change" decision lives in the IsAvailable/Enabled/IdleUnloaded
    // property setters themselves (one tested place), not sprinkled as
    // unconditional Invoke() calls at the end of every method.

    [Fact]
    public async Task AvailabilityChanged_se_dispara_al_completar_una_carga_exitosa()
    {
        engine.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns("Hola.");

        var processor = Build();
        var fireCount = 0;
        processor.AvailabilityChanged += () => fireCount++;

        await processor.ProbeAsync();

        Assert.True(processor.IsAvailable);
        Assert.Equal(1, fireCount);
    }

    [Fact]
    public async Task AvailabilityChanged_no_se_dispara_si_un_segundo_ProbeAsync_no_cambia_nada()
    {
        // The idempotent-on-success path (Un_segundo_ProbeAsync_no_repite_la_carga_si_el_primero_tuvo_exito
        // above) must not fire a second notification either — nothing about
        // the observable state changed, so the tray has nothing new to show.
        var processor = await ProbedAsync();
        var fireCount = 0;
        processor.AvailabilityChanged += () => fireCount++;

        await processor.ProbeAsync();

        Assert.Equal(0, fireCount);
    }

    [Fact]
    public async Task AvailabilityChanged_se_dispara_de_inmediato_al_cambiar_Enabled_antes_de_que_termine_la_carga()
    {
        // Finding 2's core requirement: Enabled flips synchronously the instant
        // SetEnabledAsync is called, and that flip alone — independent of
        // whatever engine.LoadAsync is still doing — must already be enough to
        // notify a subscriber, so the tray can show immediate feedback instead
        // of waiting up to ProbeTimeout for the load to settle.
        var releaseLoad = new TaskCompletionSource();
        engine.LoadAsync(Arg.Any<CancellationToken>()).Returns(async _ => await releaseLoad.Task);
        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns("Hola.");

        var processor = Build(enabled: false);
        var fireCount = 0;
        processor.AvailabilityChanged += () => fireCount++;

        var enable = processor.SetEnabledAsync(true);

        Assert.True(processor.Enabled);
        Assert.Equal(1, fireCount);

        releaseLoad.SetResult();
        await enable;
    }

    [Fact]
    public async Task AvailabilityChanged_se_dispara_al_descargar_por_inactividad_y_IdleUnloaded_queda_en_true()
    {
        var clock = new FakeTimeProvider();

        engine.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns("Hola.");
        engine.UnloadAsync(Arg.Any<CancellationToken>()).Returns(true);

        var processor = Build(idleTimeout: TimeSpan.FromMinutes(15), clock: clock);
        await processor.ProbeAsync();

        Assert.False(processor.IdleUnloaded);
        var fireCount = 0;
        processor.AvailabilityChanged += () => fireCount++;

        clock.Advance(TimeSpan.FromMinutes(15));

        for (var attempt = 0; attempt < 100 && processor.IsAvailable; attempt++)
            await Task.Delay(10);

        Assert.False(processor.IsAvailable);
        Assert.True(processor.IdleUnloaded);
        Assert.True(fireCount >= 1);
    }

    [Fact]
    public async Task IdleUnloaded_vuelve_a_falso_cuando_la_recarga_en_segundo_plano_tiene_exito()
    {
        var clock = new FakeTimeProvider();

        engine.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        engine.ChatAsync(Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<CancellationToken>())
            .Returns("Hola.");
        engine.UnloadAsync(Arg.Any<CancellationToken>()).Returns(true);

        var processor = Build(idleTimeout: TimeSpan.FromMinutes(15), clock: clock);
        await processor.ProbeAsync();

        clock.Advance(TimeSpan.FromMinutes(15));
        for (var attempt = 0; attempt < 100 && processor.IsAvailable; attempt++)
            await Task.Delay(10);

        Assert.True(processor.IdleUnloaded);

        var result = await processor.ProcessAsync("che, como andas", Context); // triggers the background reload
        Assert.Equal("che, como andas", result); // this dictation still degrades to raw text

        for (var attempt = 0; attempt < 100 && !processor.IsAvailable; attempt++)
            await Task.Delay(10);

        Assert.True(processor.IsAvailable);
        Assert.False(processor.IdleUnloaded);
    }

    [Fact]
    public async Task AvailabilityChanged_no_se_dispara_si_UnloadAsync_no_logra_liberar_el_modelo()
    {
        // Mirrors UnloadAsync_que_el_motor_no_logra_liberar_deja_el_modelo_disponible:
        // nothing actually changed (the engine refused to free it), so nothing
        // here should tell the tray otherwise.
        engine.UnloadAsync(Arg.Any<CancellationToken>()).Returns(false);

        var processor = await ProbedAsync();
        var fireCount = 0;
        processor.AvailabilityChanged += () => fireCount++;

        await processor.UnloadAsync();

        Assert.True(processor.IsAvailable);
        Assert.Equal(0, fireCount);
    }

    /// <summary>
    /// A minimal <see cref="TimeProvider"/> fake — <see cref="LlamaPostProcessor"/>
    /// only ever asks its <see cref="IdleUnloadScheduler"/> to create a
    /// one-shot timer and dispose it, so this only needs to support exactly
    /// that: firing once <see cref="Advance"/> crosses the due time, unless
    /// disposed first.
    /// </summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        private readonly List<FakeTimer> live = [];

        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state, now + dueTime);
            live.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            now += by;
            foreach (var timer in live.ToArray())
                timer.MaybeFire(now);
        }

        private sealed class FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            private bool disposed;
            private bool fired;

            public void MaybeFire(DateTimeOffset instant)
            {
                if (disposed || fired || instant < due) return;

                fired = true;
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

            public void Dispose()
            {
                disposed = true;
                owner.live.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}

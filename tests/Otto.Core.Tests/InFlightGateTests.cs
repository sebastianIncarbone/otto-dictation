using Otto.PostProcessing;

namespace Otto.Core.Tests;

/// <summary>
/// <see cref="InFlightGate"/> in isolation — no GGUF, no GPU, no LLamaSharp
/// type anywhere. This is the piece <see cref="LlamaEngine"/> uses to keep
/// <c>Dispose</c> from freeing native context/weights out from under a
/// <c>ChatAsync</c> call (or the <c>WarmUpAsync</c> call that drives the same
/// <c>ChatAsync</c>) that is still running on another thread — these tests
/// exercise exactly that race, just with a plain counter and delegate standing
/// in for "an inference is using the native handles" and "free the native
/// handles" instead of real weights/context/executor.
/// </summary>
public class InFlightGateTests
{
    [Fact]
    public void Sin_llamadas_en_vuelo_dispose_libera_de_inmediato()
    {
        var gate = new InFlightGate();

        var freedNatives = false;
        var result = gate.TryDispose(TimeSpan.FromSeconds(1), () => freedNatives = true);

        Assert.True(result);
        Assert.True(freedNatives);
    }

    [Fact]
    public void Una_llamada_nueva_es_rechazada_apenas_arranca_el_dispose()
    {
        // Models: ChatAsync calling TryEnter() after Dispose() has already run
        // (or started) — the field-free-underneath-it case Dispose exists to
        // prevent. Rejection must be immediate, not only after the bounded
        // wait elapses, so a would-be new inference never even starts once
        // shutdown has begun.
        var gate = new InFlightGate();

        gate.TryDispose(TimeSpan.FromSeconds(1), () => { });

        var entered = gate.TryEnter();

        Assert.False(entered);
    }

    [Fact]
    public async Task Dispose_espera_a_que_termine_la_llamada_en_vuelo_antes_de_liberar()
    {
        // Models: DictationPipeline.Dispose() (tray "Salir") racing a real
        // dictation's ProcessAsync while its ChatAsync call is mid-inference.
        var gate = new InFlightGate();
        Assert.True(gate.TryEnter());

        var freedNatives = false;
        var disposeTask = Task.Run(() => gate.TryDispose(TimeSpan.FromSeconds(5), () => freedNatives = true));

        // The in-flight call has not exited yet, so a bounded wait on the
        // dispose task must NOT observe completion — proving Dispose actually
        // blocks on the call instead of freeing underneath it immediately.
        var early = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromMilliseconds(300)));
        Assert.NotSame(disposeTask, early);
        Assert.False(freedNatives);

        gate.Exit();

        var finished = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(disposeTask, finished);
        Assert.True(await disposeTask);
        Assert.True(freedNatives);
    }

    [Fact]
    public async Task Dispose_espera_a_que_terminen_todas_las_llamadas_en_vuelo_antes_de_liberar()
    {
        // Models two concurrent callers sharing the same LlamaEngine — a real
        // dictation and the deferred warm-up landing at the same time.
        var gate = new InFlightGate();
        Assert.True(gate.TryEnter());
        Assert.True(gate.TryEnter());

        var freedNatives = false;
        var disposeTask = Task.Run(() => gate.TryDispose(TimeSpan.FromSeconds(5), () => freedNatives = true));

        gate.Exit();
        var early = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromMilliseconds(300)));
        Assert.NotSame(disposeTask, early);
        Assert.False(freedNatives);

        gate.Exit();
        var finished = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(disposeTask, finished);
        Assert.True(freedNatives);
    }

    [Fact]
    public void Dispose_agota_el_tiempo_de_espera_y_no_libera_si_la_llamada_sigue_en_vuelo()
    {
        // Models a hung native call (wedged driver) still in flight when
        // "Salir" is clicked: Dispose must give up rather than block forever,
        // and — the whole point of this fix — must NOT free underneath it.
        var gate = new InFlightGate();
        Assert.True(gate.TryEnter());

        var freedNatives = false;
        var result = gate.TryDispose(TimeSpan.FromMilliseconds(50), () => freedNatives = true);

        Assert.False(result);
        Assert.False(freedNatives);
    }

    [Fact]
    public void Dispose_es_idempotente()
    {
        var gate = new InFlightGate();

        var freeCount = 0;
        var first = gate.TryDispose(TimeSpan.FromSeconds(1), () => freeCount++);
        var second = gate.TryDispose(TimeSpan.FromSeconds(1), () => freeCount++);

        Assert.True(first);
        Assert.True(second);
        Assert.Equal(1, freeCount);
    }

    [Fact]
    public void Dispose_es_idempotente_incluso_si_la_primera_llamada_agoto_el_tiempo_de_espera()
    {
        // A second Dispose() call (should never happen in production — Otto
        // calls it once from the tray "Salir"/uninstall path — but must stay
        // safe) must not re-wait on a call that never exited, and must not
        // free late.
        var gate = new InFlightGate();
        Assert.True(gate.TryEnter());

        var freeCount = 0;
        var first = gate.TryDispose(TimeSpan.FromMilliseconds(50), () => freeCount++);
        var second = gate.TryDispose(TimeSpan.FromSeconds(2), () => freeCount++);

        Assert.False(first);
        Assert.True(second);
        Assert.Equal(0, freeCount);
    }
}

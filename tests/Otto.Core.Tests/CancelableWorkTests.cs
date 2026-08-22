using Otto.PostProcessing;

namespace Otto.Core.Tests;

/// <summary>
/// <see cref="CancelableWork"/> in isolation — no GGUF, no GPU. This is the piece
/// that makes <see cref="LlamaPostProcessor"/>'s <c>ProbeTimeout</c> budget real:
/// without it, a hung native load can never be cancelled, and because the
/// <c>SemaphoreSlim</c> gate around it is only released in the caller's enclosing
/// <c>finally</c>, every subsequent probe or "reintentar" click would block
/// forever waiting on a load that was never going to return in time.
/// </summary>
public class CancelableWorkTests
{
    [Fact]
    public async Task Devuelve_el_resultado_cuando_el_trabajo_termina_antes_del_token()
    {
        var ran = false;

        await CancelableWork.Run(() => ran = true, CancellationToken.None);

        Assert.True(ran);
    }

    [Fact]
    public async Task El_llamador_deja_de_esperar_cuando_el_token_se_cancela_aunque_el_trabajo_siga_colgado()
    {
        // Simulates a hung native load: the work item never returns on its own.
        // Real production code cannot forcibly interrupt LLamaWeights.LoadFromFile
        // either — the whole point of this fix is that the CALLER stops waiting at
        // the deadline instead of blocking the SemaphoreSlim gate forever, even
        // though the background work keeps running.
        var workStarted = new TaskCompletionSource();
        var neverReleased = new TaskCompletionSource();

        using var budget = new CancellationTokenSource();

        var work = CancelableWork.Run(() =>
        {
            workStarted.SetResult();
            neverReleased.Task.GetAwaiter().GetResult();
        }, budget.Token);

        await workStarted.Task;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        budget.CancelAfter(TimeSpan.FromMilliseconds(30));

        // Would hang for the lifetime of the test process without the fix — this
        // await is the actual assertion: it must return, and quickly, rather than
        // waiting for work that will never finish.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2),
            $"El llamador esperó {watch.Elapsed.TotalSeconds:F1} s en vez de cancelarse cerca de los 30 ms del presupuesto");

        neverReleased.SetResult();
    }

    [Fact]
    public async Task Propaga_una_excepcion_del_trabajo_cuando_no_hay_cancelacion()
    {
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CancelableWork.Run(() => throw new InvalidOperationException("boom"), CancellationToken.None));

        Assert.Equal("boom", thrown.Message);
    }
}

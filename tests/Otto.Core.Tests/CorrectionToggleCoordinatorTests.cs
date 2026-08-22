using Otto.App;
using Otto.Core;

namespace Otto.Core.Tests;

/// <summary>
/// <see cref="CorrectionToggleCoordinator"/> in isolation — no Avalonia, no DI
/// container, no <c>App.axaml.cs</c>. This is the piece extracted out of
/// <c>SetCorrectionEnabled</c>'s persistence tail after review round 4 found
/// it writing the call's own closure-captured <c>enabled</c> parameter to
/// disk instead of <see cref="IPostProcessor.Enabled"/>'s live value —
/// harmless for a single click, but wrong under N rapid clicks whose
/// <c>SetEnabledAsync</c> calls serialize on <c>LlamaPostProcessor</c>'s
/// <c>SemaphoreSlim</c> gate, which Microsoft documents as giving no
/// waiter-ordering guarantee: a superseded, earlier click's tail could
/// resume last and persist a value that contradicts the live state.
///
/// <see cref="SequencedPostProcessor"/> below mirrors
/// <c>LlamaPostProcessor.SetEnabledAsync</c>'s own documented contract
/// exactly — <c>Enabled</c> flips synchronously, before any await — so these
/// tests can force the exact interleaving the real gate can produce (any
/// completion order) without a GGUF or a GPU.
/// </summary>
public class CorrectionToggleCoordinatorTests
{
    private sealed class SequencedPostProcessor : IPostProcessor
    {
        public bool IsAvailable => false;
        public bool Enabled { get; private set; }
        public bool IdleUnloaded => false;
        public event Action? AvailabilityChanged { add { } remove { } }

        /// <summary>One gate per <see cref="SetEnabledAsync"/> call, in call order — the test completes them in whatever order it wants to model.</summary>
        public readonly List<TaskCompletionSource> Gates = [];

        public Task<bool> ProbeAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<string> ProcessAsync(string text, DictationContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(text);

        public void SetIdleTimeout(TimeSpan? interval) { }

        public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            // The exact ordering LlamaPostProcessor.SetEnabledAsync's own doc
            // comment describes: the flip happens synchronously, before the
            // gate-queued work below even starts.
            Enabled = enabled;

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Gates.Add(gate);
            await gate.Task;
        }
    }

    [Fact]
    public async Task Un_solo_toggle_devuelve_el_valor_que_pidio()
    {
        var postProcessor = new SequencedPostProcessor();

        var task = CorrectionToggleCoordinator.ToggleAsync(postProcessor, enabled: true);
        postProcessor.Gates[0].SetResult();

        Assert.True(await task);
    }

    [Fact]
    public async Task Un_solo_toggle_a_apagar_devuelve_false()
    {
        var postProcessor = new SequencedPostProcessor();

        var task = CorrectionToggleCoordinator.ToggleAsync(postProcessor, enabled: false);
        postProcessor.Gates[0].SetResult();

        Assert.False(await task);
    }

    /// <summary>
    /// The exact race from the blocker: three rapid clicks — disable, enable,
    /// disable, the prompt's own example — whose <c>SetEnabledAsync</c> calls
    /// are gate-serialized in production. Every one of the three calls here
    /// must report the SAME final value (the third click's, <c>false</c>),
    /// no matter which one's gate is released first: the coordinator reads
    /// <see cref="IPostProcessor.Enabled"/> AFTER its own await, never the
    /// <c>enabled</c> argument it was given.
    /// </summary>
    [Theory]
    [InlineData(0, 1, 2)] // completes in click order
    [InlineData(2, 1, 0)] // completes in reverse order — the exact shape of the reported bug
    [InlineData(1, 0, 2)] // the middle click resolves first
    public async Task Tres_clicks_rapidos_terminando_en_cualquier_orden_devuelven_el_mismo_valor_final(
        int completeFirst, int completeSecond, int completeThird)
    {
        var postProcessor = new SequencedPostProcessor();

        var click1 = CorrectionToggleCoordinator.ToggleAsync(postProcessor, enabled: false);
        var click2 = CorrectionToggleCoordinator.ToggleAsync(postProcessor, enabled: true);
        var click3 = CorrectionToggleCoordinator.ToggleAsync(postProcessor, enabled: false);

        // All three synchronous prefixes already ran — SequencedPostProcessor
        // only awaits AFTER flipping Enabled — so by this point Enabled
        // already holds click3's value, exactly as production's
        // IPostProcessor.Enabled is documented to.
        Assert.False(postProcessor.Enabled);

        foreach (var i in new[] { completeFirst, completeSecond, completeThird })
            postProcessor.Gates[i].SetResult();

        var results = await Task.WhenAll(click1, click2, click3);

        Assert.All(results, result => Assert.False(result));
    }

    /// <summary>
    /// Same race, opposite final intent — enable, disable, enable — so this
    /// is not just "false always wins by coincidence": whichever value the
    /// LAST click asked for is what every tail must agree on.
    /// </summary>
    [Theory]
    [InlineData(0, 1, 2)]
    [InlineData(2, 1, 0)]
    public async Task Tres_clicks_rapidos_terminando_en_activar_devuelven_true_en_los_tres(
        int completeFirst, int completeSecond, int completeThird)
    {
        var postProcessor = new SequencedPostProcessor();

        var click1 = CorrectionToggleCoordinator.ToggleAsync(postProcessor, enabled: true);
        var click2 = CorrectionToggleCoordinator.ToggleAsync(postProcessor, enabled: false);
        var click3 = CorrectionToggleCoordinator.ToggleAsync(postProcessor, enabled: true);

        Assert.True(postProcessor.Enabled);

        foreach (var i in new[] { completeFirst, completeSecond, completeThird })
            postProcessor.Gates[i].SetResult();

        var results = await Task.WhenAll(click1, click2, click3);

        Assert.All(results, result => Assert.True(result));
    }
}

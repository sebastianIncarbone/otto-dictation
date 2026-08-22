using Otto.PostProcessing;

namespace Otto.Core.Tests;

/// <summary>
/// <see cref="LoadGenerationTracker"/> in isolation — no GGUF, no GPU, no
/// LLamaSharp type anywhere. This is the piece <see cref="LlamaEngine"/> uses to
/// stay safe when <see cref="CancelableWork"/>'s orphaned worker (its own doc
/// comment: cancellation cannot truly interrupt the native load, so an abandoned
/// attempt keeps running in the background) finally returns after a retry has
/// already started or even finished — these tests exercise exactly that race,
/// just with plain delegates standing in for "publish these native handles" and
/// "dispose these native handles" instead of real weights/context/executor.
/// </summary>
public class LoadGenerationTrackerTests
{
    [Fact]
    public void Un_unico_intento_publica_sus_resultados()
    {
        var tracker = new LoadGenerationTracker();
        var attempt = tracker.ClaimGeneration();

        var published = false;
        var discarded = false;

        var result = tracker.TryPublish(attempt, publish: () => published = true, discard: () => discarded = true);

        Assert.True(result);
        Assert.True(published);
        Assert.False(discarded);
    }

    [Fact]
    public void Un_intento_huerfano_que_termina_despues_de_un_reintento_se_descarta_sin_publicar()
    {
        // Models: first ProbeAsync's LoadAsync hangs past ProbeTimeout and is
        // abandoned by CancelableWork; a tray "reintentar" click starts a second
        // LoadAsync (a NEW generation) which finishes and publishes FIRST; only
        // THEN does the orphaned first attempt's native call finally return.
        var tracker = new LoadGenerationTracker();
        var orphanedAttempt = tracker.ClaimGeneration();
        var retryAttempt = tracker.ClaimGeneration();

        var retryPublished = false;
        var retryResult = tracker.TryPublish(retryAttempt, publish: () => retryPublished = true, discard: () => throw new InvalidOperationException("should not discard the current attempt"));

        var orphanPublished = false;
        var orphanDiscarded = false;
        var orphanResult = tracker.TryPublish(orphanedAttempt, publish: () => orphanPublished = true, discard: () => orphanDiscarded = true);

        Assert.True(retryResult);
        Assert.True(retryPublished);

        Assert.False(orphanResult);
        Assert.False(orphanPublished);
        Assert.True(orphanDiscarded);
    }

    [Fact]
    public void Un_intento_huerfano_que_termina_antes_de_que_arranque_el_reintento_igual_se_descarta_si_ya_no_es_el_actual()
    {
        // Same race, opposite finishing order: the orphaned attempt's native call
        // returns and calls TryPublish BEFORE the retry has finished its own load
        // — but the retry has already CLAIMED its generation by then, which is
        // what must matter, not which one finishes first.
        var tracker = new LoadGenerationTracker();
        var orphanedAttempt = tracker.ClaimGeneration();
        var retryAttempt = tracker.ClaimGeneration();

        var orphanPublished = false;
        var orphanDiscarded = false;
        var orphanResult = tracker.TryPublish(orphanedAttempt, publish: () => orphanPublished = true, discard: () => orphanDiscarded = true);

        Assert.False(orphanResult);
        Assert.False(orphanPublished);
        Assert.True(orphanDiscarded);

        var retryPublished = false;
        var retryResult = tracker.TryPublish(retryAttempt, publish: () => retryPublished = true, discard: () => throw new InvalidOperationException("should not discard the current attempt"));

        Assert.True(retryResult);
        Assert.True(retryPublished);
    }

    [Fact]
    public void Dispose_descarta_un_intento_todavia_en_vuelo_en_vez_de_dejarlo_publicar()
    {
        var tracker = new LoadGenerationTracker();
        var attempt = tracker.ClaimGeneration();

        var disposedCurrent = false;
        tracker.Dispose(disposeCurrent: () => disposedCurrent = true);

        Assert.True(disposedCurrent);

        var published = false;
        var discarded = false;
        var result = tracker.TryPublish(attempt, publish: () => published = true, discard: () => discarded = true);

        Assert.False(result);
        Assert.False(published);
        Assert.True(discarded);
    }

    [Fact]
    public void Dispose_es_idempotente()
    {
        var tracker = new LoadGenerationTracker();

        var disposeCalls = 0;
        tracker.Dispose(disposeCurrent: () => disposeCalls++);
        tracker.Dispose(disposeCurrent: () => disposeCalls++);

        Assert.Equal(1, disposeCalls);
    }
}

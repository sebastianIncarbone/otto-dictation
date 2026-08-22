using Otto.PostProcessing;

namespace Otto.Core.Tests;

/// <summary>
/// <see cref="IdleUnloadScheduler"/> in isolation — no GGUF, no GPU, no real
/// wall-clock minutes. This is the piece that turns "unload after N idle
/// minutes" into an actual timer instead of a comment; <see cref="FakeTimeProvider"/>
/// stands in for real time so the scheduling DECISION is testable in
/// milliseconds.
/// </summary>
public class IdleUnloadSchedulerTests
{
    [Fact]
    public void Sin_tocar_de_nuevo_dispara_onIdle_al_vencer_el_intervalo()
    {
        var clock = new FakeTimeProvider();
        var fired = 0;

        var scheduler = new IdleUnloadScheduler(clock, () => fired++, TimeSpan.FromMinutes(15));
        scheduler.Touch();

        clock.Advance(TimeSpan.FromMinutes(15));

        Assert.Equal(1, fired);
    }

    [Fact]
    public void No_dispara_antes_de_que_venza_el_intervalo()
    {
        var clock = new FakeTimeProvider();
        var fired = 0;

        var scheduler = new IdleUnloadScheduler(clock, () => fired++, TimeSpan.FromMinutes(15));
        scheduler.Touch();

        clock.Advance(TimeSpan.FromMinutes(14));

        Assert.Equal(0, fired);
    }

    /// <summary>
    /// A correction (or a fresh load) counts as activity — Touch has to push
    /// the deadline out again, not just arm the FIRST timer.
    /// </summary>
    [Fact]
    public void Tocar_de_nuevo_antes_de_vencer_reprograma_el_vencimiento()
    {
        var clock = new FakeTimeProvider();
        var fired = 0;

        var scheduler = new IdleUnloadScheduler(clock, () => fired++, TimeSpan.FromMinutes(15));
        scheduler.Touch();

        clock.Advance(TimeSpan.FromMinutes(10));
        scheduler.Touch(); // activity — the clock restarts from here

        clock.Advance(TimeSpan.FromMinutes(10)); // 20 min since the first Touch, but only 10 since the second

        Assert.Equal(0, fired);

        clock.Advance(TimeSpan.FromMinutes(5)); // now 15 min since the second Touch

        Assert.Equal(1, fired);
    }

    /// <summary>"Nunca" — a null interval must never schedule anything, no matter how much time passes.</summary>
    [Fact]
    public void Un_intervalo_nulo_nunca_dispara()
    {
        var clock = new FakeTimeProvider();
        var fired = 0;

        var scheduler = new IdleUnloadScheduler(clock, () => fired++, interval: null);
        scheduler.Touch();

        clock.Advance(TimeSpan.FromDays(1));

        Assert.Equal(0, fired);
    }

    [Fact]
    public void Configure_cambia_el_intervalo_para_el_proximo_Touch()
    {
        var clock = new FakeTimeProvider();
        var fired = 0;

        var scheduler = new IdleUnloadScheduler(clock, () => fired++, TimeSpan.FromMinutes(15));
        scheduler.Configure(TimeSpan.FromMinutes(5));
        scheduler.Touch();

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(1, fired);
    }

    /// <summary>
    /// Reconfiguring a shorter interval while a timer is already pending
    /// reschedules it immediately, from now — the setting change is not
    /// silently deferred until the next unrelated activity.
    /// </summary>
    [Fact]
    public void Configure_reprograma_un_temporizador_ya_pendiente()
    {
        var clock = new FakeTimeProvider();
        var fired = 0;

        var scheduler = new IdleUnloadScheduler(clock, () => fired++, TimeSpan.FromMinutes(15));
        scheduler.Touch();

        clock.Advance(TimeSpan.FromMinutes(1));
        scheduler.Configure(TimeSpan.FromMinutes(2)); // reschedules from now: fires at +3 min, not +15

        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(1, fired);
    }

    /// <summary>Switching to "nunca" while a timer is pending cancels it.</summary>
    [Fact]
    public void Configure_a_nunca_cancela_un_temporizador_pendiente()
    {
        var clock = new FakeTimeProvider();
        var fired = 0;

        var scheduler = new IdleUnloadScheduler(clock, () => fired++, TimeSpan.FromMinutes(15));
        scheduler.Touch();

        scheduler.Configure(null);
        clock.Advance(TimeSpan.FromDays(1));

        Assert.Equal(0, fired);
    }

    [Fact]
    public void Stop_cancela_un_temporizador_pendiente()
    {
        var clock = new FakeTimeProvider();
        var fired = 0;

        var scheduler = new IdleUnloadScheduler(clock, () => fired++, TimeSpan.FromMinutes(15));
        scheduler.Touch();

        scheduler.Stop();
        clock.Advance(TimeSpan.FromMinutes(15));

        Assert.Equal(0, fired);
    }

    [Fact]
    public void Dispose_tambien_cancela_un_temporizador_pendiente()
    {
        var clock = new FakeTimeProvider();
        var fired = 0;

        var scheduler = new IdleUnloadScheduler(clock, () => fired++, TimeSpan.FromMinutes(15));
        scheduler.Touch();

        scheduler.Dispose();
        clock.Advance(TimeSpan.FromMinutes(15));

        Assert.Equal(0, fired);
    }

    /// <summary>
    /// A minimal <see cref="TimeProvider"/> fake: <see cref="IdleUnloadScheduler"/>
    /// only ever calls <c>CreateTimer(callback, null, dueTime, Timeout.InfiniteTimeSpan)</c>
    /// and <c>Dispose()</c> on the timer it gets back, so this only needs to
    /// support exactly that usage — a one-shot timer that fires once
    /// <see cref="Advance"/> crosses its due time, unless disposed first.
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

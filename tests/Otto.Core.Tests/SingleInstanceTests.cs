using Otto.App;

namespace Otto.Core.Tests;

/// <summary>
/// Named kernel objects are visible across the whole session, so every test
/// claims under its own name. Sharing one would make these tests fail whenever
/// the developer running them has Otto open in the tray — which is most of the
/// time, and would be the most confusing possible reason for a red build.
/// </summary>
public class SingleInstanceTests
{
    private static string Name() => $"Otto.Test.{Guid.NewGuid():N}";

    [Fact]
    public void La_primera_instancia_se_queda_con_el_lugar()
    {
        var name = Name();

        using var first = SingleInstance.Claim(name);

        Assert.NotNull(first);
    }

    [Fact]
    public void La_segunda_no_arranca_y_le_pide_la_ventana_a_la_primera()
    {
        var name = Name();
        using var first = SingleInstance.Claim(name);

        var asked = new ManualResetEventSlim();
        first!.Activated += () => asked.Set();

        var second = SingleInstance.Claim(name);

        // Null is the instruction to exit: this launch has nothing left to do.
        Assert.Null(second);

        // The wait is what proves the exit was not silent. Without the knock the
        // second launch would vanish and the user would have clicked on nothing.
        Assert.True(asked.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void La_espera_se_rearma_y_atiende_al_lanzamiento_siguiente()
    {
        var name = Name();
        using var first = SingleInstance.Claim(name);

        var asked = new AutoResetEvent(false);
        first!.Activated += () => asked.Set();

        // Uno, atendido, y recién entonces el otro. Deliberadamente en secuencia:
        // el handle es auto-reset y no lleva la cuenta, así que dos golpes antes de
        // que nadie consuma el primero son un solo evento — que es la respuesta
        // correcta, porque dos lanzamientos a la vez quieren una sola ventana. Lo
        // que este test cuida es lo otro: que después de atender uno la espera
        // vuelva a quedar armada, en vez de escuchar una única vez y no más.
        Assert.Null(SingleInstance.Claim(name));
        Assert.True(asked.WaitOne(TimeSpan.FromSeconds(5)));

        Assert.Null(SingleInstance.Claim(name));
        Assert.True(asked.WaitOne(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Cuando_la_primera_se_va_otra_puede_quedarse_con_el_lugar()
    {
        var name = Name();

        SingleInstance.Claim(name)!.Dispose();

        // Otto quitting from the tray has to leave the name free. If the mutex were
        // disposed without being released, the next launch would find the claim
        // abandoned and this would come back null.
        using var next = SingleInstance.Claim(name);

        Assert.NotNull(next);
    }
}

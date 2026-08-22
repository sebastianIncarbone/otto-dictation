using Otto.App;

namespace Otto.Core.Tests;

/// <summary>
/// The correction tray item shows one of four states, derived — not stored — from
/// three observable facts: whether the corrector is loaded (<c>IPostProcessor.IsAvailable</c>),
/// whether the GGUF is on disk, and whether the deferred load has settled at least
/// once. There is deliberately no fifth "connection" state: an in-process model has
/// no connection to lose, unlike the Ollama-era tray item this replaces.
///
/// Kept dependency-free on purpose — no <c>IPostProcessor</c>, no
/// <c>ProvisioningOptions</c>, no DI — so the whole decision is testable without the
/// Avalonia composition root this project cannot construct headlessly.
/// </summary>
public class CorrectionTrayStatesTests
{
    [Fact]
    public void Disponible_es_el_estado_activo()
    {
        var state = CorrectionTrayStates.For(isAvailable: true, modelFileExists: true, deferredLoadSettled: true, hasGpu: true);

        Assert.Equal(CorrectionTrayStateKind.Ready, state.Kind);
        Assert.Equal("Corrección: activa ✓", state.Header);
        Assert.False(state.CanRetry);
    }

    [Fact]
    public void Disponible_gana_aunque_el_archivo_figure_ausente()
    {
        // Cannot happen with the real IPostProcessor (loading requires the file), but
        // the precedence itself is part of the contract this function promises: a
        // corrector that is already loaded is never demoted back to "missing".
        var state = CorrectionTrayStates.For(isAvailable: true, modelFileExists: false, deferredLoadSettled: false, hasGpu: true);

        Assert.Equal(CorrectionTrayStateKind.Ready, state.Kind);
    }

    [Fact]
    public void Sin_el_archivo_del_modelo_el_estado_es_falta_el_modelo()
    {
        var state = CorrectionTrayStates.For(isAvailable: false, modelFileExists: false, deferredLoadSettled: true, hasGpu: true);

        Assert.Equal(CorrectionTrayStateKind.Missing, state.Kind);
        Assert.Equal("Corrección: falta el modelo — reintentar", state.Header);
        Assert.True(state.CanRetry);
    }

    /// <summary>
    /// A load that has not even started yet (background probe not launched, or the
    /// GGUF just finished downloading) also reads as Missing until the file is
    /// actually there — "reintentar" always means "get me the model", not "keep
    /// waiting".
    /// </summary>
    [Fact]
    public void Falta_el_archivo_gana_sobre_no_haber_terminado_de_cargar()
    {
        var state = CorrectionTrayStates.For(isAvailable: false, modelFileExists: false, deferredLoadSettled: false, hasGpu: true);

        Assert.Equal(CorrectionTrayStateKind.Missing, state.Kind);
    }

    [Fact]
    public void Archivo_presente_y_carga_diferida_sin_terminar_es_cargando()
    {
        var state = CorrectionTrayStates.For(isAvailable: false, modelFileExists: true, deferredLoadSettled: false, hasGpu: true);

        Assert.Equal(CorrectionTrayStateKind.Loading, state.Kind);
        Assert.Equal("Corrección: cargando…", state.Header);
        Assert.False(state.CanRetry);
    }

    [Fact]
    public void Archivo_presente_carga_terminada_y_no_disponible_es_no_se_pudo_cargar()
    {
        var state = CorrectionTrayStates.For(isAvailable: false, modelFileExists: true, deferredLoadSettled: true, hasGpu: true);

        Assert.Equal(CorrectionTrayStateKind.Failed, state.Kind);
        Assert.Equal("Corrección: no se pudo cargar — reintentar", state.Header);
        Assert.True(state.CanRetry);
    }

    /// <summary>Every state produces a header — nothing falls through to a blank menu item.</summary>
    [Theory]
    [InlineData(true, true, true, CorrectionTrayStateKind.Ready)]
    [InlineData(false, false, true, CorrectionTrayStateKind.Missing)]
    [InlineData(false, true, false, CorrectionTrayStateKind.Loading)]
    [InlineData(false, true, true, CorrectionTrayStateKind.Failed)]
    public void Los_cuatro_estados_con_gpu_producen_un_encabezado_no_vacio(
        bool isAvailable, bool modelFileExists, bool deferredLoadSettled, CorrectionTrayStateKind expected)
    {
        var state = CorrectionTrayStates.For(isAvailable, modelFileExists, deferredLoadSettled, hasGpu: true);

        Assert.Equal(expected, state.Kind);
        Assert.False(string.IsNullOrWhiteSpace(state.Header));
    }

    /// <summary>
    /// CPU-only hardware can never load the correction model inside the 2s dictation
    /// budget — see <c>ProvisioningOptions.HasGpu</c>'s own doc comment, and
    /// <c>ProvisioningOptions.CorrectionCoordinates</c>, which already refuses to even
    /// download the GGUF there. A "reintentar" that can never succeed is worse than no
    /// button at all, so the state this function returns for that hardware must never
    /// be retryable — regardless of what the other three inputs happen to be.
    /// </summary>
    [Fact]
    public void Sin_gpu_el_estado_es_no_compatible_y_no_admite_reintentar()
    {
        var state = CorrectionTrayStates.For(isAvailable: false, modelFileExists: false, deferredLoadSettled: false, hasGpu: false);

        Assert.Equal(CorrectionTrayStateKind.Unsupported, state.Kind);
        Assert.Equal("Corrección: requiere GPU", state.Header);
        Assert.False(state.CanRetry);
    }

    /// <summary>
    /// Cannot happen with the real wiring (<c>Program.cs</c> only ever registers a
    /// working <c>IPostProcessor</c> when the hardware has a GPU), but the precedence
    /// itself is the contract: a machine with no GPU reads as Unsupported even under
    /// inputs that would otherwise mean Ready, Missing, Loading or Failed. This is
    /// the property that guarantees a CPU-only machine can never produce
    /// <c>CanRetry: true</c>.
    /// </summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public void Sin_gpu_nunca_produce_un_estado_que_admita_reintentar(
        bool isAvailable, bool modelFileExists, bool deferredLoadSettled)
    {
        var state = CorrectionTrayStates.For(isAvailable, modelFileExists, deferredLoadSettled, hasGpu: false);

        Assert.Equal(CorrectionTrayStateKind.Unsupported, state.Kind);
        Assert.False(state.CanRetry);
    }
}

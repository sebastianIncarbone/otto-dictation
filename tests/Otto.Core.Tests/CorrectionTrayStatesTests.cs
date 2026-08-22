using Otto.App;

namespace Otto.Core.Tests;

/// <summary>
/// The correction tray item shows one of several states, derived — not stored —
/// from what is actually observable: whether the corrector is loaded
/// (<c>IPostProcessor.IsAvailable</c>), whether the GGUF is on disk, whether the
/// deferred load has settled at least once, whether the hardware supports it at
/// all, whether the user wants correction on, and whether the model was unloaded
/// by the idle timer specifically. There is deliberately no "connection" state:
/// an in-process model has no connection to lose, unlike the Ollama-era tray item
/// this replaces.
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
        var state = CorrectionTrayStates.For(isAvailable: true, modelFileExists: true, deferredLoadSettled: true, hasGpu: true, correctVoseo: true, idleUnloaded: false);

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
        var state = CorrectionTrayStates.For(isAvailable: true, modelFileExists: false, deferredLoadSettled: false, hasGpu: true, correctVoseo: true, idleUnloaded: false);

        Assert.Equal(CorrectionTrayStateKind.Ready, state.Kind);
    }

    [Fact]
    public void Sin_el_archivo_del_modelo_el_estado_es_falta_el_modelo()
    {
        var state = CorrectionTrayStates.For(isAvailable: false, modelFileExists: false, deferredLoadSettled: true, hasGpu: true, correctVoseo: true, idleUnloaded: false);

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
        var state = CorrectionTrayStates.For(isAvailable: false, modelFileExists: false, deferredLoadSettled: false, hasGpu: true, correctVoseo: true, idleUnloaded: false);

        Assert.Equal(CorrectionTrayStateKind.Missing, state.Kind);
    }

    [Fact]
    public void Archivo_presente_y_carga_diferida_sin_terminar_es_cargando()
    {
        var state = CorrectionTrayStates.For(isAvailable: false, modelFileExists: true, deferredLoadSettled: false, hasGpu: true, correctVoseo: true, idleUnloaded: false);

        Assert.Equal(CorrectionTrayStateKind.Loading, state.Kind);
        Assert.Equal("Corrección: cargando…", state.Header);
        Assert.False(state.CanRetry);
    }

    [Fact]
    public void Archivo_presente_carga_terminada_y_no_disponible_es_no_se_pudo_cargar()
    {
        var state = CorrectionTrayStates.For(isAvailable: false, modelFileExists: true, deferredLoadSettled: true, hasGpu: true, correctVoseo: true, idleUnloaded: false);

        Assert.Equal(CorrectionTrayStateKind.Failed, state.Kind);
        Assert.Equal("Corrección: no se pudo cargar — reintentar", state.Header);
        Assert.True(state.CanRetry);
    }

    /// <summary>Every state produces a header — nothing falls through to a blank menu item.</summary>
    [Theory]
    [InlineData(true, true, true, false, CorrectionTrayStateKind.Ready)]
    [InlineData(false, false, true, false, CorrectionTrayStateKind.Missing)]
    [InlineData(false, true, false, false, CorrectionTrayStateKind.Loading)]
    [InlineData(false, true, true, false, CorrectionTrayStateKind.Failed)]
    [InlineData(false, true, true, true, CorrectionTrayStateKind.Idle)]
    public void Los_estados_con_gpu_producen_un_encabezado_no_vacio(
        bool isAvailable, bool modelFileExists, bool deferredLoadSettled, bool idleUnloaded, CorrectionTrayStateKind expected)
    {
        var state = CorrectionTrayStates.For(isAvailable, modelFileExists, deferredLoadSettled, hasGpu: true, correctVoseo: true, idleUnloaded);

        Assert.Equal(expected, state.Kind);
        Assert.False(string.IsNullOrWhiteSpace(state.Header));
    }

    /// <summary>
    /// CPU-only hardware can never load the correction model inside the 2s dictation
    /// budget — see <c>ProvisioningOptions.HasGpu</c>'s own doc comment, and
    /// <c>ProvisioningOptions.CorrectionCoordinates</c>, which already refuses to even
    /// download the GGUF there. A "reintentar" that can never succeed is worse than no
    /// button at all, so the state this function returns for that hardware must never
    /// be retryable — regardless of what the other inputs happen to be.
    /// </summary>
    [Fact]
    public void Sin_gpu_el_estado_es_no_compatible_y_no_admite_reintentar()
    {
        var state = CorrectionTrayStates.For(isAvailable: false, modelFileExists: false, deferredLoadSettled: false, hasGpu: false, correctVoseo: true, idleUnloaded: false);

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
        var state = CorrectionTrayStates.For(isAvailable, modelFileExists, deferredLoadSettled, hasGpu: false, correctVoseo: true, idleUnloaded: false);

        Assert.Equal(CorrectionTrayStateKind.Unsupported, state.Kind);
        Assert.False(state.CanRetry);
    }

    /// <summary>hasGpu still wins over everything, including an idle unload — no GPU means truly nothing here can ever work, and a real IPostProcessor can never report IdleUnloaded on hardware it never loaded on in the first place.</summary>
    [Fact]
    public void Sin_gpu_gana_incluso_en_pausa_por_inactividad()
    {
        var state = CorrectionTrayStates.For(isAvailable: false, modelFileExists: true, deferredLoadSettled: true, hasGpu: false, correctVoseo: true, idleUnloaded: true);

        Assert.Equal(CorrectionTrayStateKind.Unsupported, state.Kind);
        Assert.False(state.CanRetry);
    }

    // ---- Off: the user switched correction off, on hardware that DOES
    // support it. New with the runtime toggle — before it, Settings.CorrectVoseo
    // == false meant the tray item was simply never built, so this state had
    // no reason to exist. Now the item stays on the menu so the user can turn
    // it back on, and this is what its header/click behaviour reads.

    [Fact]
    public void Apagado_por_el_usuario_es_el_estado_apagado()
    {
        var state = CorrectionTrayStates.For(isAvailable: false, modelFileExists: true, deferredLoadSettled: true, hasGpu: true, correctVoseo: false, idleUnloaded: false);

        Assert.Equal(CorrectionTrayStateKind.Off, state.Kind);
        Assert.Equal("Corrección: apagada — activar", state.Header);

        // CanRetry keeps its existing, narrower meaning — "a load is worth
        // kicking off in the background right now" (Missing/Failed only).
        // Turning Off back on is a SEPARATE, always-available toggle
        // App.axaml.cs's click handler drives directly off Kind, the same
        // way the character overlay's tray item toggles without consulting
        // any retry flag.
        Assert.False(state.CanRetry);
    }

    /// <summary>
    /// CorrectVoseo off wins over every OTHER input except hasGpu — even a
    /// (transiently possible, right after the toggle click, before UnloadAsync
    /// settles) IsAvailable: true still reads as Off: the header has to
    /// reflect what the user just asked for, not what has not caught up yet.
    /// </summary>
    [Theory]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, true, true, true)]
    public void Apagado_gana_sobre_los_otros_estados_activos(
        bool isAvailable, bool modelFileExists, bool deferredLoadSettled, bool idleUnloaded)
    {
        var state = CorrectionTrayStates.For(isAvailable, modelFileExists, deferredLoadSettled, hasGpu: true, correctVoseo: false, idleUnloaded);

        Assert.Equal(CorrectionTrayStateKind.Off, state.Kind);
    }

    /// <summary>hasGpu still wins over everything, including Off — no GPU means truly nothing here can ever work.</summary>
    [Fact]
    public void Sin_gpu_gana_incluso_con_la_correccion_apagada()
    {
        var state = CorrectionTrayStates.For(isAvailable: false, modelFileExists: false, deferredLoadSettled: false, hasGpu: false, correctVoseo: false, idleUnloaded: false);

        Assert.Equal(CorrectionTrayStateKind.Unsupported, state.Kind);
    }

    // ---- Idle: the model loaded successfully at least once, then the idle
    // timer unloaded it — LlamaPostProcessor.IdleUnloaded, exposed on
    // IPostProcessor specifically so this decision does not have to guess.
    // Distinct from Failed on purpose: an idle unload is Otto's own feature
    // working as designed, not a broken model, and the next dictation reloads
    // it automatically (LlamaPostProcessor.ProcessAsync's own background
    // reload trigger) — so "no se pudo cargar — reintentar" would be an
    // outright lie here, exactly the class of defect this fix pass exists to
    // close.

    [Fact]
    public void Descargado_por_inactividad_es_el_estado_en_pausa()
    {
        var state = CorrectionTrayStates.For(isAvailable: false, modelFileExists: true, deferredLoadSettled: true, hasGpu: true, correctVoseo: true, idleUnloaded: true);

        Assert.Equal(CorrectionTrayStateKind.Idle, state.Kind);
        Assert.Equal("Corrección: en pausa — se reactiva al dictar", state.Header);

        // Not retryable in the Missing/Failed sense — clicking here behaves
        // like clicking Ready/Loading (turn correction off), not like
        // "kick off a background load right now".
        Assert.False(state.CanRetry);
    }

    /// <summary>
    /// Cannot happen with the real IPostProcessor (IdleUnloaded only follows a
    /// successful load, which implies the file was there and the load
    /// settled), but the precedence is still part of the contract: an idle
    /// unload never gets demoted to Missing or re-promoted to Loading/Failed
    /// by what modelFileExists/deferredLoadSettled happen to say.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void En_pausa_gana_sobre_falta_el_modelo_cargando_y_no_se_pudo_cargar(
        bool modelFileExists, bool deferredLoadSettled)
    {
        var state = CorrectionTrayStates.For(
            isAvailable: false, modelFileExists, deferredLoadSettled, hasGpu: true, correctVoseo: true, idleUnloaded: true);

        Assert.Equal(CorrectionTrayStateKind.Idle, state.Kind);
    }

    /// <summary>
    /// Cannot happen with the real IPostProcessor either (IsAvailable and
    /// IdleUnloaded are never both true — ProbeAsync clears IdleUnloaded in
    /// the very same success path that sets IsAvailable), but Ready's own
    /// precedence must not depend on that: a loaded, working corrector is
    /// never demoted to "in pausa" by a stale flag.
    /// </summary>
    [Fact]
    public void Disponible_gana_sobre_en_pausa_por_inactividad()
    {
        var state = CorrectionTrayStates.For(isAvailable: true, modelFileExists: true, deferredLoadSettled: true, hasGpu: true, correctVoseo: true, idleUnloaded: true);

        Assert.Equal(CorrectionTrayStateKind.Ready, state.Kind);
    }
}

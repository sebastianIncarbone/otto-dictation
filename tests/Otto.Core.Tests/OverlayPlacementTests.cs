using Avalonia;
using Otto.App;

namespace Otto.Core.Tests;

/// <summary>
/// Dónde abre el personaje cuando se lo puede arrastrar.
///
/// <para>
/// El caso que justifica que esto sea una función pura y no un método de la ventana es el
/// que no se puede reproducir a mano: una posición guardada en un segundo monitor que ya
/// no está conectado. Otto abre en coordenadas que no existen, en ninguna pantalla, y el
/// usuario queda con un personaje invisible y un ajuste que no puede alcanzar para
/// arreglarlo. Un test no puede desenchufar un monitor; esto sí.
/// </para>
/// </summary>
public class OverlayPlacementTests
{
    private static readonly PixelSize Character = new(144, 144);

    /// <summary>Una pantalla 1920x1080 con la barra de tareas abajo.</summary>
    private static readonly PixelRect Principal = new(0, 0, 1920, 1040);

    /// <summary>Una segunda pantalla a la derecha de la principal.</summary>
    private static readonly PixelRect Secundaria = new(1920, 0, 1920, 1040);

    [Fact]
    public void Sin_posicion_guardada_va_al_rincon_de_abajo_a_la_derecha()
    {
        var donde = OverlayPlacement.Resolve(null, Character, [Principal]);

        Assert.Equal(new PixelPoint(1920 - 144 - 24, 1040 - 144 - 24), donde);
    }

    [Fact]
    public void Una_posicion_guardada_dentro_de_la_pantalla_se_respeta()
    {
        var elegida = new PixelPoint(300, 200);

        Assert.Equal(elegida, OverlayPlacement.Resolve(elegida, Character, [Principal]));
    }

    [Fact]
    public void Una_posicion_en_el_segundo_monitor_se_respeta_mientras_ese_monitor_exista()
    {
        var alla = new PixelPoint(2400, 300);

        Assert.Equal(alla, OverlayPlacement.Resolve(alla, Character, [Principal, Secundaria]));
    }

    [Fact]
    public void Si_se_desconecta_ese_monitor_el_personaje_vuelve_al_rincon()
    {
        // El caso entero por el que esto existe. Sin esto Otto abre fuera de toda pantalla:
        // invisible, imposible de agarrar, y sin manera de traerlo de vuelta arrastrando.
        var alla = new PixelPoint(2400, 300);

        var donde = OverlayPlacement.Resolve(alla, Character, [Principal]);

        Assert.Equal(OverlayPlacement.Corner(Principal, Character), donde);
    }

    [Fact]
    public void Una_astilla_de_un_pixel_asomando_no_cuenta_como_alcanzable()
    {
        // "Cualquier superposición" no alcanza: una ventana con un píxel adentro del
        // escritorio está técnicamente visible y en la práctica perdida, porque no hay nada
        // ahí de dónde agarrarla.
        var casiAfuera = new PixelPoint(1919, 500);

        Assert.False(OverlayPlacement.IsReachable(casiAfuera, Character, [Principal]));
        Assert.Equal(OverlayPlacement.Corner(Principal, Character),
            OverlayPlacement.Resolve(casiAfuera, Character, [Principal]));
    }

    [Fact]
    public void Una_franja_ancha_pero_finita_tampoco_alcanza()
    {
        // Se mide por eje y no por área a propósito: asomando 2 px por abajo hay 288 píxeles
        // superpuestos y ni un solo lugar al que apuntarle.
        var asomando = new PixelPoint(500, 1040 - 2);

        Assert.False(OverlayPlacement.IsReachable(asomando, Character, [Principal]));
    }

    [Fact]
    public void Justo_en_el_limite_de_lo_agarrable_se_respeta()
    {
        var alFilo = new PixelPoint(1920 - OverlayPlacement.MinimumVisible, 500);

        Assert.True(OverlayPlacement.IsReachable(alFilo, Character, [Principal]));
        Assert.Equal(alFilo, OverlayPlacement.Resolve(alFilo, Character, [Principal]));
    }

    [Fact]
    public void El_rincon_se_mide_sobre_el_tamano_real_del_overlay()
    {
        // Las tres apariencias tienen tamaños distintos — el punto mínimo son 64x24 — y el
        // rincón se recalcula al cambiarlas. Medirlo sobre un tamaño fijo dejaría al glyph
        // flotando lejos del borde.
        var glyph = new PixelSize(64, 24);

        Assert.Equal(new PixelPoint(1920 - 64 - 24, 1040 - 24 - 24),
            OverlayPlacement.Corner(Principal, glyph));
    }

    [Fact]
    public void Sin_pantallas_no_se_cae()
    {
        // Screens.All puede venir vacía mientras el shell todavía está levantando.
        var guardada = new PixelPoint(10, 10);

        Assert.Equal(guardada, OverlayPlacement.Resolve(guardada, Character, []));
        Assert.Equal(default, OverlayPlacement.Resolve(null, Character, []));
    }
}

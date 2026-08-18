using Otto.App;
using Otto.Core;

namespace Otto.Core.Tests;

/// <summary>
/// <see cref="HotkeyLabels"/> is the single place a <see cref="HotkeyBinding"/> becomes
/// the Spanish text the user reads. Pinning down "fixed order" and "total" here is what
/// makes the label a function of the binding instead of a second, driftable source of
/// truth — the defect class this whole change exists to close.
/// </summary>
public class HotkeyLabelsTests
{
    [Theory]
    [InlineData(HotkeyModifiers.Alt | HotkeyModifiers.Control, 0x4B, "Ctrl+Alt+K")]
    [InlineData(HotkeyModifiers.Windows | HotkeyModifiers.Shift | HotkeyModifiers.Alt | HotkeyModifiers.Control, 0x31, "Ctrl+Alt+Shift+Win+1")]
    public void For_ordena_los_modificadores_siempre_Ctrl_Alt_Shift_Win_sin_importar_la_entrada(
        HotkeyModifiers modificadores, uint tecla, string esperado)
    {
        // Alt|Control and Control|Alt are the same [Flags] value, but the point is that
        // the label never depends on press order either — it reads flags, not events.
        Assert.Equal(esperado, HotkeyLabels.For(new HotkeyBinding(modificadores, tecla)));
    }

    [Fact]
    public void For_traduce_el_espacio_por_defecto_a_espanol()
    {
        Assert.Equal("Ctrl+Alt+Espacio", HotkeyLabels.For(HotkeyBinding.Default));
    }

    [Theory]
    [InlineData(HotkeyModifiers.None, 0x41, "A")]
    [InlineData(HotkeyModifiers.Control, 0x35, "Ctrl+5")]
    [InlineData(HotkeyModifiers.Shift, 0x70, "Shift+F1")]
    [InlineData(HotkeyModifiers.Windows, 0x0D, "Win+Enter")]
    public void For_es_total_sobre_combinaciones_representables(HotkeyModifiers modificadores, uint tecla, string esperado)
    {
        // "Total" means every one of these produces a real, non-empty label — no
        // exception, no blank string — which is the regression this pins down.
        var etiqueta = HotkeyLabels.For(new HotkeyBinding(modificadores, tecla));

        Assert.False(string.IsNullOrWhiteSpace(etiqueta));
        Assert.Equal(esperado, etiqueta);
    }

    [Fact]
    public void For_una_tecla_sin_nombre_conocido_cae_en_una_alternativa_total_y_no_explota()
    {
        // 0xFE names no key Otto recognises. The fallback has to be ugly-but-total,
        // never an exception or a silently dropped key.
        Assert.Equal("Ctrl+Tecla 0xFE", HotkeyLabels.For(new HotkeyBinding(HotkeyModifiers.Control, 0xFE)));
    }

    [Fact]
    public void ForModifiers_sin_modificadores_devuelve_vacio()
    {
        Assert.Equal("", HotkeyLabels.ForModifiers(HotkeyModifiers.None));
    }

    [Fact]
    public void ForModifiers_ordena_igual_que_For_para_el_hint_en_vivo()
    {
        Assert.Equal("Ctrl+Alt", HotkeyLabels.ForModifiers(HotkeyModifiers.Alt | HotkeyModifiers.Control));
    }

    [Theory]
    [InlineData(0x10)] // VK_SHIFT
    [InlineData(0x11)] // VK_CONTROL
    [InlineData(0x12)] // VK_MENU (Alt)
    [InlineData(0x5B)] // VK_LWIN
    [InlineData(0xA3)] // VK_RCONTROL — one left/right variant, to prove those are covered too
    public void IsModifierKey_reconoce_los_cuatro_modificadores_y_sus_variantes(uint tecla)
    {
        Assert.True(HotkeyLabels.IsModifierKey(tecla));
    }

    [Fact]
    public void IsModifierKey_devuelve_falso_para_una_tecla_normal()
    {
        Assert.False(HotkeyLabels.IsModifierKey(0x4B)); // K
    }

    [Theory]
    [InlineData(0x11, HotkeyModifiers.Control)]
    [InlineData(0x12, HotkeyModifiers.Alt)]
    [InlineData(0x10, HotkeyModifiers.Shift)]
    [InlineData(0x5B, HotkeyModifiers.Windows)]
    public void ImpliedModifier_traduce_la_tecla_cruda_a_su_bandera(uint tecla, HotkeyModifiers esperado)
    {
        Assert.Equal(esperado, HotkeyLabels.ImpliedModifier(tecla));
    }

    [Fact]
    public void ImpliedModifier_devuelve_None_para_una_tecla_que_no_es_modificador()
    {
        Assert.Equal(HotkeyModifiers.None, HotkeyLabels.ImpliedModifier(0x4B)); // K
    }
}

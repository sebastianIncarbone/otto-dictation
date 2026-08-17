using Otto.PostProcessing;

namespace Otto.Core.Tests;

/// <summary>
/// The guard rail that decides whether a model's correction is safe to insert.
///
/// Each case here is a real output from the milestone 4 benchmark. Being wrong
/// about voseo is a nuisance; being wrong about what the user said is a broken
/// tool, so these lean towards rejecting.
/// </summary>
public class EditGuardTests
{
    [Fact]
    public void Acepta_una_correccion_de_voseo()
    {
        Assert.True(EditGuard.IsSafe(
            "Che, ¿me puedes revisar el pull request que subí recién?",
            "Che, ¿me podés revisar el pull request que subí recién?"));
    }

    [Fact]
    public void Acepta_dos_imperativos_corregidos()
    {
        Assert.True(EditGuard.IsSafe(
            "Instala las dependencias con pnpm install y después levanta el docker compose.",
            "Instalá las dependencias con pnpm install y después levantá el docker compose."));
    }

    [Fact]
    public void Acepta_que_no_haya_cambiado_nada()
    {
        const string text = "Primero revisamos la latencia y después vemos la precisión.";

        Assert.True(EditGuard.IsSafe(text, text));
    }

    [Fact]
    public void Rechaza_una_traduccion_al_portugues()
    {
        // Salida real de qwen2.5:3b con el prompt viejo.
        Assert.False(EditGuard.IsSafe(
            "El componente de React no está haciendo el rerender cuando cambia el estado.",
            "El componente de React não está fazendo o rerender quando o estado muda."));
    }

    [Fact]
    public void Rechaza_cuando_el_modelo_contesta_en_vez_de_corregir()
    {
        Assert.False(EditGuard.IsSafe(
            "Che, ¿me puedes revisar el pull request?",
            "¡Claro! Veo que te has olvidado de añadir el voseo al verbo revisa. " +
            "Aquí está el texto corregido: Che, ¿me podés revisar el pull request?"));
    }

    [Fact]
    public void Rechaza_una_salida_vacia()
    {
        Assert.False(EditGuard.IsSafe("Hay algo acá", "   "));
    }

    [Fact]
    public void Rechaza_cuando_se_come_media_frase()
    {
        Assert.False(EditGuard.IsSafe(
            "Hay que hacer el deploy a producción después del merge y avisarle al equipo.",
            "Hay que hacer el deploy."));
    }

    [Fact]
    public void El_acento_cuenta_como_cambio()
    {
        // Si el guardián normalizara los acentos sería ciego justo a lo que cuida:
        // el voseo se marca con la tilde.
        Assert.Equal(1, EditGuard.WordsTouched("corre el build", "corré el build"));
    }

    [Fact]
    public void Una_frase_corta_tiene_margen_minimo()
    {
        // Con el 20% a secas, una frase de tres palabras no podría cambiar ninguna.
        Assert.True(EditGuard.IsSafe("instala el paquete", "instalá el paquete"));
    }
}

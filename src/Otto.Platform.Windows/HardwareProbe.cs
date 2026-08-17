namespace Otto.Platform.Windows;

public enum Acceleration { Gpu, Cpu }

/// <summary>
/// Decides which speech model to download, before downloading it.
///
/// This matters more than it looks. Measured on the development machine,
/// <c>large-v3-turbo</c> takes 0,7 s on the GPU and <b>17 s</b> on the CPU for the
/// same five-second phrase. Someone who gets the GPU model on a laptop without one
/// does not conclude "my hardware is modest" — they conclude the tool is broken,
/// and they are not wrong. See docs/distribucion-y-primer-arranque.md, trampa 4.
///
/// The test is deliberately crude: whether the Vulkan loader is present. Any GPU
/// that exposes Vulkan — including integrated ones — beats the CPU by an order of
/// magnitude, so the interesting question is not which GPU but whether there is
/// one at all. Loading a model to find out is not an option, since the answer
/// decides which model to download.
/// </summary>
public static class HardwareProbe
{
    public static Acceleration Detect() =>
        File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll"))
            ? Acceleration.Gpu
            : Acceleration.Cpu;

    /// <summary>
    /// Fallback on CPU is <c>base</c>, not <c>small</c>: <c>small</c> was measured
    /// at 3,5 s for a short clip, which already blows the budget.
    /// </summary>
    public static (string Model, string FileName, string Size) Recommend(Acceleration acceleration) =>
        acceleration == Acceleration.Gpu
            ? ("large-v3-turbo", "ggml-large-v3-turbo.bin", "~1,6 GB")
            : ("base", "ggml-base.bin", "~150 MB");

    public static string Explain(Acceleration acceleration) => acceleration == Acceleration.Gpu
        ? "Se detectó aceleración por GPU. Se usa large-v3-turbo, el modelo más preciso."
        : "No se detectó GPU. Se usa el modelo base: large-v3-turbo tardaría más de 15 segundos por dictado en esta máquina.";
}

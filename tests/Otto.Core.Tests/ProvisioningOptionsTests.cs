using Otto.Speech;

namespace Otto.Core.Tests;

/// <summary>
/// <see cref="ProvisioningOptions.CorrectionCoordinates"/> is what Program.cs calls
/// instead of assigning the four <c>Correction*</c> properties directly, so the
/// "does this hardware support the correction leg at all" decision is a plain
/// static method reachable with no DI container and no mocks.
///
/// Gated on <see cref="ProvisioningOptions.HasGpu"/> alone, NOT on
/// <c>Settings.CorrectVoseo</c> — a deliberate change from this method's first
/// cut. <c>ProvisioningOptions</c> is built once, at startup, and correction can
/// now be switched on at runtime (the Settings checkbox, the tray toggle): if
/// this method kept gating on the STARTUP value of CorrectVoseo, a GPU user who
/// started with it off would have <c>CorrectionFileName</c>/<c>CorrectionUrl</c>
/// permanently null for the rest of the process — the GGUF could never be
/// downloaded even after they turned correction back on, because
/// <c>ModelProvisioner.ProvisionAsync</c>'s own third leg has nothing to fetch
/// without them. Respecting the CURRENT value of CorrectVoseo at the moment
/// download would actually run — so a machine with the feature switched off
/// never eagerly downloads a ~2 GB model for it — is <see cref="ModelProvisioner.NeedsProvisioning"/>
/// and <see cref="ModelProvisioner.ProvisionAsync"/>'s own job now, both of
/// which take a live <c>correctionEnabled</c> parameter for exactly this reason
/// — see <see cref="ModelProvisionerTests"/>.
/// </summary>
public class ProvisioningOptionsTests
{
    private const string FileName = "qwen2.5-3b-instruct-q4_k_m.gguf";
    private const string Url = "https://huggingface.co/example.gguf";
    private const string Label = "qwen2.5-3b-instruct";
    private const string Size = "~2 GB";

    [Fact]
    public void Devuelve_las_coordenadas_cuando_hay_GPU()
    {
        var (fileName, url, label, size) = ProvisioningOptions.CorrectionCoordinates(
            hasGpu: true, fileName: FileName, url: Url, label: Label, size: Size);

        Assert.Equal(FileName, fileName);
        Assert.Equal(Url, url);
        Assert.Equal(Label, label);
        Assert.Equal(Size, size);
    }

    [Fact]
    public void Devuelve_todo_null_sin_GPU()
    {
        // Same gate ModelProvisioner's own HasGpu check already applies: a 3B
        // model can never land inside the 2s dictation budget on CPU, so there
        // is nothing to download regardless of what CorrectVoseo says.
        var (fileName, url, label, size) = ProvisioningOptions.CorrectionCoordinates(
            hasGpu: false, fileName: FileName, url: Url, label: Label, size: Size);

        Assert.Null(fileName);
        Assert.Null(url);
        Assert.Null(label);
        Assert.Null(size);
    }
}

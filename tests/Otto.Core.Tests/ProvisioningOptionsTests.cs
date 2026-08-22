using Otto.Speech;

namespace Otto.Core.Tests;

/// <summary>
/// <see cref="ProvisioningOptions.CorrectionCoordinates"/> is what Program.cs calls
/// instead of assigning the four <c>Correction*</c> properties directly, so the
/// "should the GGUF be offered at all" decision — hardware AND the user's own
/// <c>Settings.CorrectVoseo</c> preference — is a plain static method reachable with
/// no DI container, no <c>Settings</c> type (<c>Otto.Speech</c> must not reference
/// <c>Otto.App</c>), and no mocks.
/// </summary>
public class ProvisioningOptionsTests
{
    private const string FileName = "qwen2.5-3b-instruct-q4_k_m.gguf";
    private const string Url = "https://huggingface.co/example.gguf";
    private const string Label = "qwen2.5-3b-instruct";
    private const string Size = "~2 GB";

    [Fact]
    public void Devuelve_las_coordenadas_cuando_hay_GPU_y_CorrectVoseo_esta_activo()
    {
        var (fileName, url, label, size) = ProvisioningOptions.CorrectionCoordinates(
            hasGpu: true, correctVoseo: true, fileName: FileName, url: Url, label: Label, size: Size);

        Assert.Equal(FileName, fileName);
        Assert.Equal(Url, url);
        Assert.Equal(Label, label);
        Assert.Equal(Size, size);
    }

    [Fact]
    public void Devuelve_todo_null_si_CorrectVoseo_esta_apagado_aunque_haya_GPU()
    {
        // The exact bug this closes: a GPU user who turned correction off in
        // Settings must not still get the ~2 GB GGUF downloaded on next launch.
        var (fileName, url, label, size) = ProvisioningOptions.CorrectionCoordinates(
            hasGpu: true, correctVoseo: false, fileName: FileName, url: Url, label: Label, size: Size);

        Assert.Null(fileName);
        Assert.Null(url);
        Assert.Null(label);
        Assert.Null(size);
    }

    [Fact]
    public void Devuelve_todo_null_sin_GPU_aunque_CorrectVoseo_este_activo()
    {
        // Same gate ModelProvisioner's own HasGpu check already applies —
        // triangulated separately from the CorrectVoseo case above, so
        // neither condition alone can make the other unnecessary.
        var (fileName, url, label, size) = ProvisioningOptions.CorrectionCoordinates(
            hasGpu: false, correctVoseo: true, fileName: FileName, url: Url, label: Label, size: Size);

        Assert.Null(fileName);
        Assert.Null(url);
        Assert.Null(label);
        Assert.Null(size);
    }
}

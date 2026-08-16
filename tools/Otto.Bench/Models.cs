using Whisper.net.Ggml;

namespace Otto.Bench;

/// <summary>
/// The Whisper variants under test, plus the Silero VAD model.
///
/// distil-* variants are deliberately absent: they are English-only and Otto's
/// hardest requirement is Rioplatense Spanish with English technical terms.
/// </summary>
public sealed record ModelSpec(string Key, GgmlType Type, string Label, string ApproxSize)
{
    public string FileName => $"ggml-{Key}.bin";
}

public static class Models
{
    public static readonly IReadOnlyList<ModelSpec> All =
    [
        new("large-v3-turbo", GgmlType.LargeV3Turbo, "large-v3-turbo", "~1,6 GB"),
        new("medium",         GgmlType.Medium,       "medium",         "~1,5 GB"),
        new("small",          GgmlType.Small,        "small",          "~0,5 GB"),
        new("base",           GgmlType.Base,         "base",           "~0,15 GB"),
    ];

    public const string VadFileName = "silero-vad.bin";

    public static IEnumerable<ModelSpec> Resolve(string? csv) => csv is null
        ? All
        : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Select(k => All.FirstOrDefault(m => m.Key.Equals(k, StringComparison.OrdinalIgnoreCase))
                          ?? throw new ArgumentException($"Modelo desconocido: '{k}'"));

    public static async Task DownloadAsync(string modelsDir, IEnumerable<ModelSpec> models)
    {
        Directory.CreateDirectory(modelsDir);
        var downloader = WhisperGgmlDownloader.Default;

        foreach (var model in models)
        {
            var path = Path.Combine(modelsDir, model.FileName);
            if (File.Exists(path))
            {
                Console.WriteLine($"  ✓ {model.Label} (ya está)");
                continue;
            }

            Console.WriteLine($"  ↓ {model.Label} ({model.ApproxSize})…");
            await SaveAsync(await downloader.GetGgmlModelAsync(model.Type), path);
        }

        var vadPath = Path.Combine(modelsDir, VadFileName);
        if (File.Exists(vadPath))
        {
            Console.WriteLine("  ✓ Silero VAD (ya está)");
        }
        else
        {
            Console.WriteLine("  ↓ Silero VAD…");
            await SaveAsync(await downloader.GetGgmlSileroVadModelAsync(SileroVadType.V5_1_2), vadPath);
        }
    }

    /// <summary>
    /// Writes to a temporary file first so an interrupted download never leaves a
    /// truncated .bin that fails later in confusing ways.
    /// </summary>
    private static async Task SaveAsync(Stream source, string path)
    {
        var temp = path + ".part";

        await using (var file = File.Create(temp))
            await source.CopyToAsync(file);

        File.Move(temp, path, overwrite: true);
    }
}

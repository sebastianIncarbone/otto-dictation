using System.Globalization;
using System.Text;

namespace Otto.Bench;

/// <summary>
/// Renders the run as Markdown. This output is the deliverable of milestone 0 and
/// feeds the benchmark table in the README — measured evidence rather than claims.
/// </summary>
public static class Report
{
    public static string Render(string runtime, string runtimeInfo, bool vad, IReadOnlyList<ModelResult> results, string clipsDir)
    {
        var clips = TestClips.Load(clipsDir);

        var md = new StringBuilder();
        var culture = new CultureInfo("es-AR");

        md.AppendLine("# Hito 0 — Resultados");
        md.AppendLine();
        md.AppendLine($"- **Runtime solicitado:** `{runtime}`");
        md.AppendLine($"- **Runtime resuelto:** `{runtimeInfo}`");
        md.AppendLine($"- **VAD:** {(vad ? "activado" : "desactivado")}");
        md.AppendLine();

        md.AppendLine("## Latencia");
        md.AppendLine();
        md.AppendLine("| Modelo | Carga | Mediana | Peor caso | Factor tiempo real |");
        md.AppendLine("|---|---:|---:|---:|---:|");

        foreach (var result in results)
        {
            var spoken = result.Clips.Where(c => !c.Clip.ExpectsSilence).ToList();
            if (spoken.Count == 0) continue;

            var times = spoken.Select(c => c.InferenceSeconds).OrderBy(t => t).ToList();

            md.AppendLine(string.Format(culture,
                "| `{0}` | {1:F1} s | {2:F2} s | {3:F2} s | {4:F3} |",
                result.Model.Label,
                result.LoadSeconds,
                Median(times),
                times[^1],
                spoken.Sum(c => c.InferenceSeconds) / spoken.Sum(c => c.AudioSeconds)));
        }

        md.AppendLine();
        md.AppendLine("> Presupuesto: **< 1,5 s** de punta a punta (ADR 0001 §5.3). La transcripción");
        md.AppendLine("> es una parte de eso — todavía faltan captura, inyección y post-proceso.");
        md.AppendLine();

        md.AppendLine("## Precisión");
        md.AppendLine();
        md.AppendLine("| Clip | Categoría | " + string.Join(" | ", results.Select(r => $"`{r.Model.Label}` WER")) + " |");
        md.AppendLine("|---|---|" + string.Concat(results.Select(_ => "---:|")));

        foreach (var clip in clips.Where(c => !c.ExpectsSilence))
        {
            var cells = results.Select(r =>
            {
                var hit = r.Clips.FirstOrDefault(c => c.Clip.Id == clip.Id);
                return hit is null ? "—" : (hit.Wer).ToString("P0", culture);
            });

            md.AppendLine($"| `{clip.Id}` | {clip.Category} | {string.Join(" | ", cells)} |");
        }

        md.AppendLine();
        md.AppendLine("## Alucinación con silencio");
        md.AppendLine();
        md.AppendLine("Estos clips tienen que dar vacío. Cualquier palabra acá es texto inventado");
        md.AppendLine("que se le escribiría al usuario en el documento. Ver ADR 0001 §5.7.");
        md.AppendLine();
        md.AppendLine("| Clip | " + string.Join(" | ", results.Select(r => $"`{r.Model.Label}`")) + " |");
        md.AppendLine("|---|" + string.Concat(results.Select(_ => "---|")));

        foreach (var clip in clips.Where(c => c.ExpectsSilence))
        {
            var cells = results.Select(r =>
            {
                var hit = r.Clips.FirstOrDefault(c => c.Clip.Id == clip.Id);
                if (hit is null) return "—";
                return hit.HallucinatedWords == 0
                    ? "✓ vacío"
                    : $"**{hit.HallucinatedWords} palabras inventadas**";
            });

            md.AppendLine($"| `{clip.Id}` | {string.Join(" | ", cells)} |");
        }

        md.AppendLine();
        md.AppendLine("## Transcripciones");
        md.AppendLine();
        md.AppendLine("El WER ordena; leer el texto decide. Acá está lo que dijo cada modelo.");
        md.AppendLine();

        foreach (var clip in clips)
        {
            md.AppendLine($"### `{clip.Id}` — {clip.Category}");
            md.AppendLine();
            md.AppendLine($"**Se dijo:** {(clip.ExpectsSilence ? "_(silencio)_" : clip.Reference)}");
            md.AppendLine();

            foreach (var result in results)
            {
                var hit = result.Clips.FirstOrDefault(c => c.Clip.Id == clip.Id);
                if (hit is null) continue;

                var text = string.IsNullOrWhiteSpace(hit.Text) ? "_(vacío)_" : hit.Text;
                md.AppendLine($"- `{result.Model.Label}` → {text}");
            }

            md.AppendLine();
        }

        return md.ToString();
    }

    private static double Median(IReadOnlyList<double> sorted) => sorted.Count % 2 == 1
        ? sorted[sorted.Count / 2]
        : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
}

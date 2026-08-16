using System.Globalization;
using System.Text;

namespace Otto.Bench;

public sealed record PromptRun(PromptVariant Variant, ModelResult Result);

/// <summary>
/// Milestone 0.5 output: the same clips and the same model under different
/// `initial_prompt` values, so any change in WER is attributable to the prompt.
/// </summary>
public static class PromptReport
{
    public static string Render(string model, string runtime, IReadOnlyList<PromptRun> runs, string clipsDir)
    {
        var clips = TestClips.Load(clipsDir).Where(c => !c.ExpectsSilence).ToList();
        var culture = new CultureInfo("es-AR");
        var md = new StringBuilder();

        md.AppendLine("# Hito 0.5 — Efecto del `initial_prompt`");
        md.AppendLine();
        md.AppendLine($"- **Modelo:** `{model}`");
        md.AppendLine($"- **Runtime:** `{runtime}`");
        md.AppendLine();
        md.AppendLine("Mismos clips, mismo modelo. La única variable es el prompt, así que");
        md.AppendLine("cualquier diferencia de WER es atribuible a él.");
        md.AppendLine();

        Table("Precisión (WER tolerante — ignora acentos)", c => c.Wer);

        md.AppendLine("El voseo se marca con el acento, así que la métrica de arriba **no lo ve**:");
        md.AppendLine("`Corré` y `Corre` le resultan idénticos. La de abajo sí lo ve.");
        md.AppendLine();

        Table("Precisión (WER estricto — cuenta acentos)", c => c.WerStrict);

        void Table(string title, Func<ClipResult, double> metric)
        {
            md.AppendLine($"## {title}");
            md.AppendLine();
            md.AppendLine("| Clip | " + string.Join(" | ", runs.Select(r => r.Variant.Label)) + " |");
            md.AppendLine("|---|" + string.Concat(runs.Select(_ => "---:|")));

            foreach (var clip in clips)
            {
                var cells = runs.Select(r =>
                    r.Result.Clips.FirstOrDefault(c => c.Clip.Id == clip.Id) is { } hit
                        ? metric(hit).ToString("P0", culture)
                        : "—");

                md.AppendLine($"| `{clip.Id}` | {string.Join(" | ", cells)} |");
            }

            md.AppendLine("| **Promedio** | " + string.Join(" | ", runs.Select(r =>
            {
                var scored = r.Result.Clips.Where(c => !c.Clip.ExpectsSilence).ToList();
                return scored.Count == 0 ? "—" : scored.Average(metric).ToString("P0", culture);
            })) + " |");

            md.AppendLine();
        }

        md.AppendLine("## Latencia");
        md.AppendLine();
        md.AppendLine("Un prompt ocupa contexto del decodificador, así que puede costar tiempo.");
        md.AppendLine();
        md.AppendLine("| Prompt | Mediana | Peor caso |");
        md.AppendLine("|---|---:|---:|");

        foreach (var run in runs)
        {
            var times = run.Result.Clips
                .Where(c => !c.Clip.ExpectsSilence)
                .Select(c => c.InferenceSeconds)
                .OrderBy(t => t)
                .ToList();

            if (times.Count == 0) continue;

            md.AppendLine(string.Format(culture, "| {0} | {1:F2} s | {2:F2} s |",
                run.Variant.Label, times[times.Count / 2], times[^1]));
        }

        md.AppendLine();
        md.AppendLine("## Transcripciones");
        md.AppendLine();

        foreach (var clip in clips)
        {
            md.AppendLine($"### `{clip.Id}` — {clip.Category}");
            md.AppendLine();
            md.AppendLine($"**Se dijo:** {clip.Reference}");
            md.AppendLine();

            foreach (var run in runs)
            {
                var hit = run.Result.Clips.FirstOrDefault(c => c.Clip.Id == clip.Id);
                if (hit is null) continue;

                var text = string.IsNullOrWhiteSpace(hit.Text) ? "_(vacío)_" : hit.Text;
                md.AppendLine($"- **{run.Variant.Label}** → {text}");
            }

            md.AppendLine();
        }

        md.AppendLine("## Prompts usados");
        md.AppendLine();

        foreach (var run in runs.Where(r => r.Variant.Text is not null))
        {
            md.AppendLine($"**{run.Variant.Label}**");
            md.AppendLine();
            md.AppendLine("```");
            md.AppendLine(run.Variant.Text);
            md.AppendLine("```");
            md.AppendLine();
        }

        return md.ToString();
    }
}

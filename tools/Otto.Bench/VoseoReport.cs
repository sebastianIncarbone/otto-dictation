using System.Globalization;
using System.Text;

namespace Otto.Bench;

public sealed record VoseoResult(
    TestClip Clip,
    string Transcription,
    string Corrected,
    double Seconds)
{
    public double WerBefore => Accuracy.WordErrorRateStrict(Clip.Reference, Transcription);
    public double WerAfter => Accuracy.WordErrorRateStrict(Clip.Reference, Corrected);

    public int Touched => Voseo.WordsTouched(Transcription, Corrected);
    public int Needed => Voseo.WordsNeeded(Transcription, Clip.Reference);

    public bool Safe => Voseo.IsSafe(Transcription, Corrected);

    /// <summary>What Otto would actually insert: the correction only if it passed the guard rail.</summary>
    public string Effective => Safe ? Corrected : Transcription;

    public double WerEffective => Accuracy.WordErrorRateStrict(Clip.Reference, Effective);
}

public sealed record VoseoRun(string Model, IReadOnlyList<VoseoResult> Results);

public static class VoseoReport
{
    public static string Render(IReadOnlyList<VoseoRun> runs)
    {
        var culture = new CultureInfo("es-AR");
        var md = new StringBuilder();

        md.AppendLine("# Hito 4 — Post-procesamiento de voseo");
        md.AppendLine();
        md.AppendLine("Mismo texto de entrada para todos los modelos: la transcripción cruda de");
        md.AppendLine("`large-v3-turbo`. La única variable es el modelo que corrige.");
        md.AppendLine();

        md.AppendLine("## Resumen");
        md.AppendLine();
        md.AppendLine("| Modelo | WER sin tocar | WER crudo del LLM | **WER con reja** | Rechazados | Latencia mediana |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|");

        foreach (var run in runs)
        {
            var scored = run.Results.Where(r => !r.Clip.ExpectsSilence).ToList();
            if (scored.Count == 0) continue;

            var times = scored.Select(r => r.Seconds).OrderBy(t => t).ToList();

            md.AppendLine(string.Format(culture,
                "| `{0}` | {1:P0} | {2:P0} | **{3:P0}** | {4} de {5} | {6:F2} s |",
                run.Model,
                scored.Average(r => r.WerBefore),
                scored.Average(r => r.WerAfter),
                scored.Average(r => r.WerEffective),
                scored.Count(r => !r.Safe),
                scored.Count,
                times[times.Count / 2]));
        }

        md.AppendLine();
        md.AppendLine("**WER con reja** es la única columna que describe el producto: la corrección");
        md.AppendLine("se usa solo si pasó el control de sobre-edición, y si no se inserta el texto");
        md.AppendLine("crudo de Whisper. Nunca puede quedar peor que no hacer nada.");

        md.AppendLine();
        md.AppendLine("**Sobre-edición** es la métrica que decide. Un modelo que toca veinte");
        md.AppendLine("palabras para arreglar dos no está corrigiendo: está reescribiendo lo que");
        md.AppendLine("dijiste, y eso es peor que dejar el voseo mal.");
        md.AppendLine();

        md.AppendLine("## Presupuesto");
        md.AppendLine();
        md.AppendLine("El post-proceso tiene 2 s de timeout (ADR §5.3). Lo que no entra, no sirve.");
        md.AppendLine();
        md.AppendLine("| Modelo | Clips dentro de 2 s |");
        md.AppendLine("|---|---:|");

        foreach (var run in runs)
        {
            var scored = run.Results.Where(r => !r.Clip.ExpectsSilence).ToList();
            if (scored.Count == 0) continue;

            var within = scored.Count(r => r.Seconds <= 2);
            md.AppendLine($"| `{run.Model}` | {within} de {scored.Count} |");
        }

        md.AppendLine();
        md.AppendLine("## Clip por clip");
        md.AppendLine();

        foreach (var clip in TestClips.All.Where(c => !c.ExpectsSilence))
        {
            var any = runs.SelectMany(r => r.Results).Any(r => r.Clip.Id == clip.Id);
            if (!any) continue;

            md.AppendLine($"### `{clip.Id}`");
            md.AppendLine();
            md.AppendLine($"**Dijiste:** {clip.Reference}");
            md.AppendLine();

            var transcription = runs
                .SelectMany(r => r.Results)
                .FirstOrDefault(r => r.Clip.Id == clip.Id)?.Transcription;

            md.AppendLine($"**Whisper:** {transcription}");
            md.AppendLine();

            foreach (var run in runs)
            {
                if (run.Results.FirstOrDefault(r => r.Clip.Id == clip.Id) is not { } hit) continue;

                var flag = hit.Safe ? "" : "  ⚠ **sobre-editó**";
                md.AppendLine($"- `{run.Model}` ({hit.Seconds:F2} s, {hit.Touched} palabras) → {hit.Corrected}{flag}");
            }

            md.AppendLine();
        }

        return md.ToString();
    }
}

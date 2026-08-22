using Microsoft.Extensions.Logging;
using Otto.Bench;
using Otto.PostProcessing;
using Otto.Speech;

// Milestone 0 harness. Records a fixed set of utterances once, then measures every
// Whisper variant against those same recordings so a comparison reflects the model
// and not the speaker. See docs/adr/0001-stack-tecnologico.md §5.8.

var command = args.FirstOrDefault()?.ToLowerInvariant();

var clipsDir  = Flag("--clips")  ?? "clips";
var modelsDir = Flag("--models-dir") ?? "models";

try
{
    switch (command)
    {
        case "record":
            Recorder.RecordAll(clipsDir, Flag("--only"), args.Contains("--force"));
            break;

        case "models":
            Console.WriteLine("Descargando modelos…");
            await Models.DownloadAsync(modelsDir, Models.Resolve(Flag("--models")));
            Console.WriteLine("Listo.");
            break;

        case "probe":
            // With --runtime this is the child process testing one runtime in
            // isolation; without it, the parent that spawns one child per runtime.
            return Flag("--runtime") is { } target ? Probe.RunChild(target) : Run(Probe.RunAll);

        case "review":
            await RunReviewAsync();
            break;

        case "prompts":
            await RunPromptsAsync();
            break;

        case "voseo":
            await RunVoseoAsync();
            break;

        case "descarga":
            await RunDownloadTestAsync();
            break;

        case "bench":
            await RunBenchAsync();
            break;

        default:
            Usage();
            return 1;
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

async Task RunBenchAsync()
{
    var runtime  = Flag("--runtime") ?? "cpu";
    var language = Flag("--lang") ?? "es";
    var useVad   = !args.Contains("--no-vad");
    var output   = Flag("--out") ?? $"resultados-{runtime}{(useVad ? "" : "-sin-vad")}.md";

    var runtimeInfo = Benchmark.ConfigureRuntime(runtime);

    Console.WriteLine($"Runtime pedido : {runtime}");
    Console.WriteLine($"Runtime activo : {runtimeInfo}");
    Console.WriteLine($"VAD            : {(useVad ? "sí" : "no")}");
    Console.WriteLine($"Idioma         : {language}");
    Console.WriteLine();

    var benchmark = new Benchmark(clipsDir, modelsDir, useVad);
    var results = new List<ModelResult>();

    foreach (var model in Models.Resolve(Flag("--models")))
    {
        Console.WriteLine($"  {model.Label}");
        results.Add(await benchmark.RunAsync(model, language));
        Console.WriteLine();
    }

    await File.WriteAllTextAsync(output, Report.Render(runtime, runtimeInfo, useVad, results, clipsDir));

    Console.WriteLine($"Reporte escrito en {Path.GetFullPath(output)}");
}

// Verifies that an interrupted model download resumes instead of starting over.
//
// Worth exercising rather than assuming: it is the user's first contact with Otto,
// it moves over a gigabyte, and the failure mode — silently restarting from zero —
// looks identical to working correctly until you watch the byte count.
async Task RunDownloadTestAsync()
{
    // DownloadAsync takes an absolute URL now (ModelDownloader no longer hardcodes
    // whisper.cpp's repo), so this harness composes it itself instead of relying on
    // a base URL baked into the downloader.
    const string url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin";

    var dir = Path.Combine(Path.GetTempPath(), "otto-descarga-test");
    Directory.CreateDirectory(dir);

    var destination = Path.Combine(dir, "ggml-base.bin");
    var partial = destination + ".part";

    foreach (var file in Directory.GetFiles(dir)) File.Delete(file);

    using var factory = LoggerFactory.Create(b => b
        .AddSimpleConsole(o => o.SingleLine = true)
        .SetMinimumLevel(LogLevel.Information));

    var log = factory.CreateLogger<ModelDownloader>();

    Console.WriteLine("=== intento 1: se corta a los 40 MB ===");

    using (var downloader = new ModelDownloader(log))
    using (var cancel = new CancellationTokenSource())
    {
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.Downloaded > 40L * 1024 * 1024) cancel.Cancel();
        });

        try
        {
            await downloader.DownloadAsync(url, destination, progress, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("  cortado a propósito");
        }
    }

    var afterFirst = File.Exists(partial) ? new FileInfo(partial).Length : 0;

    Console.WriteLine($"  parcial en disco : {afterFirst / 1024d / 1024:N1} MB");
    Console.WriteLine($"  final existe     : {File.Exists(destination)}");
    Console.WriteLine();
    Console.WriteLine("=== intento 2: tiene que continuar, no empezar de cero ===");

    using (var downloader = new ModelDownloader(log))
        await downloader.DownloadAsync(url, destination, null);

    var final = new FileInfo(destination).Length;

    Console.WriteLine($"  final            : {final / 1024d / 1024:N1} MB");
    Console.WriteLine($"  .part suelto     : {File.Exists(partial)}");
    Console.WriteLine();
    Console.WriteLine(afterFirst > 0 && final > afterFirst && !File.Exists(partial)
        ? "REANUDACIÓN OK"
        : "FALLÓ");

    Directory.Delete(dir, recursive: true);
}

// Milestone 4: which local model can fix the voseo without rewriting the dictation.
// Transcribes once, then feeds that same text to every candidate, so the only
// variable is the corrector.
//
// Points at the exact ICorrectionEngine/LlamaEngine stack Otto.PostProcessing ships
// in production, so this measures what users actually get rather than a stand-in
// HTTP client. Ollama is gone from the product: there is nothing left to probe or
// list installed models from, so --models now takes GGUF file names inside
// modelsDir instead of Ollama tags, and defaults to the one model Otto ships.
async Task RunVoseoAsync()
{
    const string CorrectionModelFileName = "qwen2.5-3b-instruct-q4_k_m.gguf";

    var candidates = Flag("--models")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     ?? [CorrectionModelFileName];

    Console.WriteLine($"Modelos a probar: {string.Join(", ", candidates)}");
    Console.WriteLine();

    Benchmark.ConfigureRuntime(Flag("--runtime") ?? "vulkan");

    using var loggerFactory = LoggerFactory.Create(b => b
        .AddSimpleConsole(o => o.SingleLine = true)
        .SetMinimumLevel(LogLevel.Information));

    using var benchmark = new Benchmark(clipsDir, modelsDir, useVad: true);
    var speech = Models.Resolve("large-v3-turbo").First();

    Console.WriteLine("Transcribiendo una vez…");
    var transcribed = await benchmark.RunAsync(speech, Flag("--lang") ?? "es");
    Console.WriteLine();

    var runs = new List<VoseoRun>();

    foreach (var model in candidates)
    {
        Console.WriteLine($"  {model}");

        var modelPath = Path.Combine(modelsDir, model);

        if (!File.Exists(modelPath))
        {
            Console.WriteLine($"    no está en {modelPath}.");
            Console.WriteLine("    Copiá el GGUF desde %LOCALAPPDATA%\\Otto\\models\\ (si ya instalaste Otto)");
            Console.WriteLine("    o descargalo de https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF");
            continue;
        }

        using var engine = new LlamaEngine(
            new PostProcessingOptions { ModelPath = modelPath },
            loggerFactory.CreateLogger<LlamaEngine>());

        try
        {
            await engine.LoadAsync();
        }
        catch (InvalidOperationException ex)
        {
            // LlamaEngine.LoadAsync guards against reconfiguring an already-loaded
            // NativeLibraryConfig, so this should no longer fire even across
            // several candidates in this same process — kept as a distinct branch
            // so a regression of THAT guard is reported as what it is (an engine
            // that could not be configured) instead of masquerading as a missing
            // or corrupt GGUF, which is exactly the failure this loop used to
            // silently mislabel every candidate after the first as.
            Console.WriteLine($"    no se pudo configurar el motor: {ex.Message}");
            continue;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    el GGUF no cargó (¿falta o está corrupto?): {ex.Message}");
            continue;
        }

        var results = new List<VoseoResult>();

        foreach (var clip in transcribed.Clips.Where(c => !c.Clip.ExpectsSilence))
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();

            string corrected;
            try
            {
                corrected = await engine.ChatAsync(Voseo.Messages(clip.Text));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    {clip.Clip.Id,-14} error: {ex.Message}");
                continue;
            }

            watch.Stop();

            results.Add(new VoseoResult(clip.Clip, clip.Text, corrected, watch.Elapsed.TotalSeconds));
            Console.WriteLine($"    {clip.Clip.Id,-14} {watch.Elapsed.TotalSeconds,6:F2}s");
        }

        runs.Add(new VoseoRun(model, results));
        Console.WriteLine();
    }

    var output = Flag("--out") ?? "resultados-voseo.md";
    await File.WriteAllTextAsync(output, VoseoReport.Render(runs));

    Console.WriteLine($"Reporte escrito en {Path.GetFullPath(output)}");
}

/// <summary>
/// Milestone 0.5: run the same clips under each candidate prompt so the effect of
/// the prompt — on accuracy and on latency — is isolated and measurable.
/// </summary>
async Task RunPromptsAsync()
{
    var runtime = Flag("--runtime") ?? "vulkan";
    var model = Models.Resolve(Flag("--models") ?? "large-v3-turbo").First();
    var language = Flag("--lang") ?? "es";
    var output = Flag("--out") ?? "resultados-prompts.md";

    var runtimeInfo = Benchmark.ConfigureRuntime(runtime);

    Console.WriteLine($"Modelo  : {model.Label}");
    Console.WriteLine($"Runtime : {runtimeInfo}");
    Console.WriteLine();

    using var benchmark = new Benchmark(clipsDir, modelsDir, useVad: true);
    var runs = new List<PromptRun>();

    foreach (var variant in Prompts.All)
    {
        Console.WriteLine($"  {variant.Label}");
        runs.Add(new PromptRun(variant, await benchmark.RunAsync(model, language, variant.Text)));
        Console.WriteLine();
    }

    await File.WriteAllTextAsync(output, PromptReport.Render(model.Label, runtime, runs, clipsDir));

    Console.WriteLine($"Reporte escrito en {Path.GetFullPath(output)}");
}

/// <summary>
/// The built-in reference is the script the speaker was asked to read. Natural
/// speech drifts from a script — an added pronoun, a different connector — and
/// scoring against the script charges that drift to the model. This prints script
/// against transcription so the reference can be corrected to what was really said.
/// </summary>
async Task RunReviewAsync()
{
    var runtime = Flag("--runtime") ?? "vulkan";
    var model = Models.Resolve(Flag("--models") ?? "large-v3-turbo").First();

    Benchmark.ConfigureRuntime(runtime);

    using var benchmark = new Benchmark(clipsDir, modelsDir, useVad: true);
    var result = await benchmark.RunAsync(model, Flag("--lang") ?? "es");

    Console.WriteLine();

    foreach (var clip in result.Clips.Where(c => !c.Clip.ExpectsSilence))
    {
        Console.WriteLine($"── {clip.Clip.Id}");
        Console.WriteLine($"   guion : {clip.Clip.Reference}");
        Console.WriteLine($"   dijo  : {(string.IsNullOrWhiteSpace(clip.Text) ? "(vacío)" : clip.Text)}");
        Console.WriteLine();
    }

    var path = TestClips.WriteOverrideTemplate(
        clipsDir,
        result.Clips.ToDictionary(c => c.Clip.Id, c => c.Text));

    Console.WriteLine("Se escribió el archivo de referencias, ya cargado con lo que el modelo");
    Console.WriteLine("escuchó. No hace falta reescribir nada de memoria: solo corregí las");
    Console.WriteLine("palabras donde el modelo se equivocó y dejá el resto como está.");
    Console.WriteLine();
    Console.WriteLine($"  {Path.GetFullPath(path)}");
    Console.WriteLine();
    Console.WriteLine("Después corré 'bench': el WER va a medir el modelo, no el guion.");
}

int Run(Action action)
{
    action();
    return 0;
}

string? Flag(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

void Usage()
{
    Console.WriteLine("""
        Otto.Bench — hito 0: medir latencia y precisión antes de construir nada.

        COMANDOS

          probe      Muestra qué runtimes cargan realmente en esta máquina
          models     Descarga los modelos de Whisper y el de Silero VAD
          record     Graba los clips de prueba (una sola vez, con tu voz)
          review     Compara guion vs. transcripción y deja corregir el guion
          bench      Corre todos los modelos contra los clips grabados
          prompts    Mide el efecto del initial_prompt (hito 0.5)

        OPCIONES

          --runtime <cuda|vulkan|cpu|auto>  Runtime a usar en bench (default: cpu)
          --models <a,b,c>              Modelos: large-v3-turbo, medium, small, base
          --no-vad                      Desactiva el VAD (para ver las alucinaciones)
          --lang <código>               Idioma (default: es)
          --only <id>                   Grabar un solo clip
          --force                       Regrabar sin preguntar por cada clip
          --clips <dir>                 Carpeta de clips (default: clips)
          --models-dir <dir>            Carpeta de modelos (default: models)
          --out <archivo>               Reporte de salida

        FLUJO

          1. otto-bench models --models large-v3-turbo,small
          2. otto-bench record
          3. otto-bench bench --runtime cuda
          4. otto-bench bench --runtime cpu
          5. otto-bench bench --runtime cuda --no-vad   (comparar alucinaciones)

        Un runtime por ejecución: la librería nativa se carga una sola vez por
        proceso, así que no se puede alternar dentro de la misma corrida.
        """);
}

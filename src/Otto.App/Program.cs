using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Otto.App;
using Otto.Core;
using Otto.Platform.Windows;
using Otto.Speech;
using Otto.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Whisper.net.Ggml;

// The console defaults to the OEM code page, which mangles every accent in the
// output of a tool that exists to transcribe Spanish.
Console.OutputEncoding = Encoding.UTF8;

// Milestone 1 host: hotkey → record → transcribe → type into the focused window.
// A console for now; the Avalonia shell arrives in milestone 3. The pipeline lives
// in Otto.Core and does not know which of the two is hosting it.

var dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Otto");

var modelsDir = Path.Combine(dataDir, "models");
var modelPath = Path.Combine(modelsDir, "ggml-large-v3-turbo.bin");
var vadPath = Path.Combine(modelsDir, "silero-vad.bin");
var databasePath = Path.Combine(dataDir, "otto.db");

// Consultar el historial no necesita cargar 1,6 GB de modelo ni registrar un hotkey.
if (args.Length > 0 && args[0] is "notas" or "buscar")
    return await NotesCommandAsync(args, databasePath);

await EnsureModelsAsync(modelsDir, modelPath, vadPath);

var services = new ServiceCollection();

services.AddLogging(builder => builder
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information));

services.AddSingleton(new TranscriberOptions { ModelPath = modelPath, VadModelPath = vadPath });
services.AddSingleton<IPromptProvider>(new ContextPromptProvider());
services.AddSingleton<ITranscriber, WhisperTranscriber>();
services.AddSingleton<IHotkeyService, PollingHotkeyService>();

// --selftest <archivo.wav> sustituye el micrófono por una grabación, para poder
// verificar el pipeline completo sin depender de que alguien hable en el momento justo.
var selfTestClip = args.SkipWhile(a => a != "--selftest").Skip(1).FirstOrDefault();

if (selfTestClip is not null)
    services.AddSingleton<IAudioCapture>(new FileAudioCapture(selfTestClip));
else
    services.AddSingleton<IAudioCapture, WasapiAudioCapture>();
services.AddSingleton<ITextInjector, ClipboardTextInjector>();
services.AddSingleton<IForegroundWindow, ForegroundWindowInspector>();
services.AddSingleton<INoteRepository>(sp =>
    new SqliteNoteRepository(databasePath, sp.GetRequiredService<ILogger<SqliteNoteRepository>>()));
services.AddSingleton<DictationPipeline>();

await using var provider = services.BuildServiceProvider();

using var pipeline = provider.GetRequiredService<DictationPipeline>();

pipeline.StateChanged += state => Console.WriteLine(state switch
{
    DictationState.Loading      => "  … cargando modelo",
    DictationState.Idle         => "  ○ listo",
    DictationState.Recording    => "  ● grabando",
    DictationState.Transcribing => "  ◐ transcribiendo",
    _ => state.ToString(),
});

pipeline.Dictated += (text, context) =>
    Console.WriteLine($"  → [{context.ProcessName}] {text}");

pipeline.Saved += note => Console.WriteLine($"  ✎ guardada como nota #{note.Id}");

Console.WriteLine("Otto — hito 1");
Console.WriteLine(selfTestClip is null
    ? "Mantené Ctrl+Alt+Espacio, hablá, soltá. Ctrl+C para salir."
    : $"Modo prueba: el micrófono se reemplaza por {Path.GetFileName(selfTestClip)}.");
Console.WriteLine();

await pipeline.StartAsync(HotkeyBinding.Default);

var quit = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.SetResult(); };
await quit.Task;

Console.WriteLine("Chau.");
return 0;

// A console stand-in for the notes screen that arrives with the UI in milestone 3.
// Enough to prove the history is real and searchable before there is anywhere to
// look at it.
static async Task<int> NotesCommandAsync(string[] args, string databasePath)
{
    using var repository = new SqliteNoteRepository(databasePath, NullLogger<SqliteNoteRepository>.Instance);

    var query = args.Length > 1 ? string.Join(' ', args[1..]) : "";

    var notes = args[0] == "buscar"
        ? await repository.SearchAsync(query, limit: 20)
        : await repository.RecentAsync(limit: 20);

    if (notes.Count == 0)
    {
        Console.WriteLine(args[0] == "buscar" ? $"Nada que coincida con «{query}»." : "Todavía no hay notas.");
        return 0;
    }

    foreach (var note in notes)
    {
        var heading = string.IsNullOrWhiteSpace(note.Title) ? "(sin título)" : note.Title;

        Console.WriteLine();
        Console.WriteLine($"#{note.Id}  {heading}");
        Console.WriteLine($"   {note.CreatedAt.ToLocalTime():dd/MM/yyyy HH:mm} · {note.Context.ProcessName} · {note.AudioDuration.TotalSeconds:F1} s");
        Console.WriteLine($"   {note.Text}");
    }

    Console.WriteLine();
    Console.WriteLine($"{notes.Count} nota(s).");

    return 0;
}

static async Task EnsureModelsAsync(string dir, string modelPath, string vadPath)
{
    Directory.CreateDirectory(dir);

    if (File.Exists(modelPath) && File.Exists(vadPath)) return;

    var downloader = WhisperGgmlDownloader.Default;

    if (!File.Exists(modelPath))
    {
        Console.WriteLine("Descargando large-v3-turbo (~1,6 GB), solo la primera vez…");
        await SaveAsync(await downloader.GetGgmlModelAsync(GgmlType.LargeV3Turbo), modelPath);
    }

    if (!File.Exists(vadPath))
    {
        Console.WriteLine("Descargando Silero VAD…");
        await SaveAsync(await downloader.GetGgmlSileroVadModelAsync(SileroVadType.V5_1_2), vadPath);
    }

    // Write to a temporary name first so an interrupted download never leaves a
    // truncated .bin that fails later in confusing ways.
    static async Task SaveAsync(Stream source, string path)
    {
        var temp = path + ".part";

        await using (var file = File.Create(temp))
            await source.CopyToAsync(file);

        File.Move(temp, path, overwrite: true);
    }
}

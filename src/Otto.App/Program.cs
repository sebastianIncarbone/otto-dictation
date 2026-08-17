using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Otto.App;
using Otto.App.ViewModels;
using Otto.Core;
using Otto.Platform.Windows;
using Otto.Speech;
using Otto.PostProcessing;
using Otto.Storage;
using Whisper.net.Ggml;

// Otto arranca minimizado en la bandeja. No hay ventana hasta que la pedís.

var dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Otto");

var modelsDir = Path.Combine(dataDir, "models");
var vadPath = Path.Combine(modelsDir, "silero-vad.bin");
var databasePath = Path.Combine(dataDir, "otto.db");

// Qué modelo bajar se decide ANTES de bajarlo, y depende de si esta máquina
// tiene GPU: son 1,6 GB contra 150 MB, y 0,7 s por dictado contra 17 s.
var acceleration = HardwareProbe.Detect();
var (speechModel, speechFile, speechSize) = HardwareProbe.Recommend(acceleration);
var modelPath = Path.Combine(modelsDir, speechFile);

await EnsureModelsAsync(modelsDir, modelPath, vadPath, speechFile, speechModel, speechSize);

var settingsStore = new SettingsStore(SettingsStore.DefaultPath);
var settings = settingsStore.Load();

// Writing the defaults straight away means the first-run window shows once and
// not on every launch.
if (settings.IsFirstRun) settingsStore.Save(settings);

var services = new ServiceCollection();

services.AddLogging(builder => builder
    .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
    .SetMinimumLevel(LogLevel.Information));

services.AddSingleton(settingsStore);
services.AddSingleton(settings);

services.AddSingleton(new TranscriberOptions
{
    ModelPath = modelPath,
    VadModelPath = vadPath,
    Language = settings.Language,
});

services.AddSingleton<IPromptProvider>(new ContextPromptProvider());
services.AddSingleton<ITranscriber, WhisperTranscriber>();
services.AddSingleton<IHotkeyService, PollingHotkeyService>();
services.AddSingleton<ITextInjector, ClipboardTextInjector>();
services.AddSingleton<IForegroundWindow, ForegroundWindowInspector>();
services.AddSingleton<IOverlayStyler, OverlayStyler>();

// El post-procesamiento es opcional: si no hay un modelo local escuchando, Otto
// funciona igual con la salida cruda de Whisper.
services.AddSingleton(new PostProcessingOptions { Model = settings.PostProcessingModel });
services.AddSingleton<IPostProcessor>(sp => settings.CorrectVoseo
    ? new OllamaPostProcessor(
        sp.GetRequiredService<PostProcessingOptions>(),
        sp.GetRequiredService<ILogger<OllamaPostProcessor>>())
    : new NullPostProcessor());
services.AddSingleton<INoteRepository>(sp =>
    new SqliteNoteRepository(databasePath, sp.GetRequiredService<ILogger<SqliteNoteRepository>>()));
services.AddSingleton<DictationPipeline>();

// --selftest <archivo.wav> sustituye el micrófono por una grabación, para poder
// verificar el pipeline sin depender de que alguien hable en el momento justo.
var selfTestClip = args.SkipWhile(a => a != "--selftest").Skip(1).FirstOrDefault();

if (selfTestClip is not null)
    services.AddSingleton<IAudioCapture>(new FileAudioCapture(selfTestClip));
else
    services.AddSingleton<IAudioCapture, WasapiAudioCapture>();

services.AddSingleton(sp => new MainViewModel(
    sp.GetRequiredService<INoteRepository>(),
    sp.GetRequiredService<DictationPipeline>(),
    sp.GetRequiredService<SettingsStore>(),
    sp.GetRequiredService<Settings>(),
    databasePath,
    // Resolved lazily: the clipboard belongs to a window, and the view model is
    // built before any window exists.
    () => App.Shell?.Clipboard));

App.Services = services.BuildServiceProvider();

BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

static AppBuilder BuildAvaloniaApp() => AppBuilder
    .Configure<App>()
    .UsePlatformDetect()
    .WithInterFont()
    .LogToTrace();

static async Task EnsureModelsAsync(
    string dir, string modelPath, string vadPath, string fileName, string model, string size)
{
    Directory.CreateDirectory(dir);

    if (File.Exists(modelPath) && File.Exists(vadPath)) return;

    if (!File.Exists(modelPath))
    {
        Console.WriteLine($"Descargando {model} ({size}), solo la primera vez…");
        Console.WriteLine("Si se corta, la próxima vez continúa desde donde quedó.");

        using var downloader = new ModelDownloader(NullLogger<ModelDownloader>.Instance);

        var progress = new Progress<DownloadProgress>(p =>
            Console.Write($"\r  {p.Fraction:P0}  ({p.BytesPerSecond / 1024 / 1024:N1} MB/s)   "));

        await downloader.DownloadAsync(fileName, modelPath, progress);
        Console.WriteLine();
    }

    // El VAD pesa un mega: reanudar no aporta nada y el descargador de la
    // librería ya sabe de dónde traerlo.
    if (!File.Exists(vadPath))
    {
        var stream = await WhisperGgmlDownloader.Default.GetGgmlSileroVadModelAsync(SileroVadType.V5_1_2);
        var temp = vadPath + ".part";

        await using (var file = File.Create(temp))
            await stream.CopyToAsync(file);

        File.Move(temp, vadPath, overwrite: true);
    }
}

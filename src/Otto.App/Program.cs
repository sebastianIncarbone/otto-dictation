using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Otto.App;
using Otto.App.ViewModels;
using Otto.Core;
using Otto.Platform.Windows;
using Otto.Speech;
using Otto.Storage;
using Whisper.net.Ggml;

// Otto arranca minimizado en la bandeja. No hay ventana hasta que la pedís.

var dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Otto");

var modelsDir = Path.Combine(dataDir, "models");
var modelPath = Path.Combine(modelsDir, "ggml-large-v3-turbo.bin");
var vadPath = Path.Combine(modelsDir, "silero-vad.bin");
var databasePath = Path.Combine(dataDir, "otto.db");

await EnsureModelsAsync(modelsDir, modelPath, vadPath);

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

static async Task EnsureModelsAsync(string dir, string modelPath, string vadPath)
{
    Directory.CreateDirectory(dir);

    if (File.Exists(modelPath) && File.Exists(vadPath)) return;

    var downloader = WhisperGgmlDownloader.Default;

    if (!File.Exists(modelPath))
        await SaveAsync(await downloader.GetGgmlModelAsync(GgmlType.LargeV3Turbo), modelPath);

    if (!File.Exists(vadPath))
        await SaveAsync(await downloader.GetGgmlSileroVadModelAsync(SileroVadType.V5_1_2), vadPath);

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

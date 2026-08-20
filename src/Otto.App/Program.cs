using Avalonia;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Otto.App;
using Otto.App.ViewModels;
using Otto.Core;
using Otto.Platform.Windows;
using Otto.Speech;
using Otto.PostProcessing;
using Otto.Storage;

/// <summary>
/// The entry point is a real method rather than top-level statements, and the
/// reason is <c>[STAThread]</c>: attributes cannot be attached to the Main the
/// compiler generates for a top-level program.
///
/// Without it the process thread is MTA, <c>OleInitialize</c> answers
/// RPC_E_CHANGED_MODE, and every OLE-backed surface Avalonia offers — the
/// clipboard above all — throws the moment it is touched. That is not a
/// degraded copy button: the exception surfaces on the UI thread with nothing
/// above it to catch it, so copying a note took the whole tray app down with
/// it. The apartment cannot be corrected later either — by the time any code
/// runs, the thread is already MTA and <c>TrySetApartmentState</c> returns
/// false.
///
/// Otto's own <c>ClipboardTextInjector</c> is unaffected because it talks to
/// the raw Win32 clipboard, which has no apartment requirement — which is
/// exactly why dictation kept working while the notes window did not.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Otto starts minimised to the tray. There is no window until you ask for one.
        //
        // Launching Otto again is one of the ways of asking. Claimed here, ahead of the
        // GPU probe and the service graph below, so a duplicate launch costs a mutex and
        // a signal rather than a second copy of everything Otto owns.
        var instance = SingleInstance.Claim();
        if (instance is null) return;

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Otto");

        var modelsDir = Path.Combine(dataDir, "models");
        var databasePath = Path.Combine(dataDir, "otto.db");

        // Which model to download is decided BEFORE downloading it, and depends on
        // whether this machine has a GPU: 1.6 GB against 150 MB, and 0.7 s per
        // dictation against 17 s.
        var acceleration = HardwareProbe.Detect();
        var (speechModel, speechFile, speechSize) = HardwareProbe.Recommend(acceleration);

        var provisioningOptions = new ProvisioningOptions
        {
            ModelsDirectory = modelsDir,
            SpeechFileName = speechFile,
            VadFileName = "silero-vad.bin",
            Label = speechModel,
            Size = speechSize,
        };

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
        services.AddSingleton(instance);

        services.AddSingleton(provisioningOptions);
        services.AddSingleton<IModelSource, HuggingFaceModelSource>();
        services.AddSingleton<ModelProvisioner>();

        // Built from provisioningOptions rather than computed separately, so the
        // transcriber and the downloader can never disagree about where the model is.
        services.AddSingleton(new TranscriberOptions
        {
            ModelPath = provisioningOptions.SpeechPath,
            VadModelPath = provisioningOptions.VadPath,
            Language = settings.Language,
        });

        services.AddSingleton<IPromptProvider>(new ContextPromptProvider());
        services.AddSingleton<ITranscriber, WhisperTranscriber>();
        services.AddSingleton<IHotkeyService, PollingHotkeyService>();
        services.AddSingleton<IHotkeyAvailability, HotkeyAvailabilityProbe>();
        services.AddSingleton<ITextInjector, ClipboardTextInjector>();
        services.AddSingleton<IForegroundWindow, ForegroundWindowInspector>();
        services.AddSingleton<IOverlayStyler, OverlayStyler>();

        // Post-processing is optional: with no local model listening, Otto works just
        // the same on Whisper's raw output.
        services.AddSingleton(new PostProcessingOptions { Model = settings.PostProcessingModel });
        services.AddSingleton<IPostProcessor>(sp => settings.CorrectVoseo
            ? new OllamaPostProcessor(
                sp.GetRequiredService<PostProcessingOptions>(),
                sp.GetRequiredService<ILogger<OllamaPostProcessor>>())
            : new NullPostProcessor());
        services.AddSingleton<INoteRepository>(sp =>
            new SqliteNoteRepository(databasePath, sp.GetRequiredService<ILogger<SqliteNoteRepository>>()));
        services.AddSingleton<DictationPipeline>();

        // --selftest <file.wav> swaps the microphone for a recording, so the pipeline
        // can be exercised without depending on somebody speaking at the right moment.
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
            () => App.Shell?.Clipboard,
            sp.GetRequiredService<ProvisioningOptions>(),
            sp.GetRequiredService<IHotkeyAvailability>(),

            // Same story as the clipboard: the save dialog is a window's, and this is the
            // one place that has any business knowing which window.
            () => App.Shell?.StorageProvider));

        App.Services = services.BuildServiceProvider();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Archivo is the default family, not Inter.
    ///
    /// It was <c>WithInterFont()</c>, which made Inter the font of every surface that
    /// does not name one — and Inter is the face the redesign explicitly steers away
    /// from. Setting the default here rather than on each control means a element
    /// nobody restyled still lands on the design's typeface instead of quietly
    /// reverting the whole redesign one unstyled label at a time.
    ///
    /// Named by family rather than by file: the collection is the whole
    /// <c>Assets/fonts</c> folder, so the faces beside it — the expanded display cut,
    /// the mono — resolve from the same place through Theme/Tokens.axaml.
    /// </summary>
    private static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .With(new FontManagerOptions
        {
            DefaultFamilyName = "avares://Otto.App/Assets/fonts#Archivo",
        })
        .LogToTrace();
}

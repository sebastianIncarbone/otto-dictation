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

        // Before the probe, before the settings, before the service graph: from
        // here on anything that throws is written down. Otto is a WinExe, so the
        // console sink below is a no-op in every real run — this is the record.
        var logFile = new LogFile(Path.Combine(dataDir, "logs", "otto.log"));
        Crash.Install(logFile);

        // Which model to download is decided BEFORE downloading it, and depends on
        // whether this machine has a GPU: 1.6 GB against 150 MB, and 0.7 s per
        // dictation against 17 s.
        var acceleration = HardwareProbe.Detect();
        var (speechModel, speechFile, speechSize) = HardwareProbe.Recommend(acceleration);

        var settingsStore = new SettingsStore(SettingsStore.DefaultPath);
        var settings = settingsStore.Load();

        // Writing the defaults straight away means the first-run window shows once and
        // not on every launch.
        if (settings.IsFirstRun) settingsStore.Save(settings);

        // Qwen2.5-3B-Instruct, Q4_K_M quantization — small enough to load
        // quickly and still land Rioplatense correction inside the 2s
        // dictation budget. Modeled on the same coordinates the
        // tools/Otto.Bench spike validated. Settings has to be loaded
        // before this point (see above) so CorrectionCoordinates can gate
        // on settings.CorrectVoseo too — a GPU user who explicitly turned
        // correction off must not still have Otto silently download ~2 GB
        // for it on next launch.
        var (correctionFileName, correctionUrl, correctionLabel, correctionSize) = ProvisioningOptions.CorrectionCoordinates(
            hasGpu: acceleration == Acceleration.Gpu,
            fileName: "qwen2.5-3b-instruct-q4_k_m.gguf",
            url: "https://huggingface.co/Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/main/qwen2.5-3b-instruct-q4_k_m.gguf",
            label: "qwen2.5-3b-instruct",
            size: "~2 GB");

        var provisioningOptions = new ProvisioningOptions
        {
            ModelsDirectory = modelsDir,
            SpeechFileName = speechFile,
            VadFileName = "silero-vad.bin",
            Label = speechModel,
            Size = speechSize,

            // Same probe, same reasoning as the speech model above: a 3B
            // correction model can never fit inside the 2s dictation budget on
            // CPU, so ModelProvisioner skips the leg entirely there rather than
            // downloading ~2 GB nobody can use.
            HasGpu = acceleration == Acceleration.Gpu,

            CorrectionFileName = correctionFileName,
            CorrectionUrl = correctionUrl,
            CorrectionLabel = correctionLabel,
            CorrectionSize = correctionSize,
        };

        var services = new ServiceCollection();

        services.AddLogging(builder => builder
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
            .AddProvider(new FileLoggerProvider(logFile))
            .SetMinimumLevel(LogLevel.Information));

        // Registered so the UI-thread handler, which is installed once Avalonia is
        // up, reports through the same file as everything else.
        services.AddSingleton(logFile);

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

        // Post-processing is optional: with no local model loaded, Otto works just
        // the same on Whisper's raw output. Skipped entirely — not merely left
        // unprobed — on CPU-only hardware, the same role HardwareProbe already
        // plays for the speech model above: a 3B model can never land inside the
        // 2s dictation budget there, so there is no point paying for the load.
        //
        // 0 minutes ("nunca" in Ajustes) maps to a null interval — see
        // PostProcessingOptions.IdleUnloadInterval's own doc comment for why
        // null, not zero, is what "never unload" has to mean here: TimeSpan.Zero
        // would be a real, immediate deadline instead of the absence of one.
        services.AddSingleton(new PostProcessingOptions
        {
            ModelPath = provisioningOptions.CorrectionPath ?? "",
            IdleUnloadInterval = settings.CorrectionIdleUnloadMinutes > 0
                ? TimeSpan.FromMinutes(settings.CorrectionIdleUnloadMinutes)
                : null,
        });

        // Gated on hardware alone now, NOT on settings.CorrectVoseo — the exact
        // "startup decision" trap this feature exists to close. CorrectVoseo can
        // be switched on at RUNTIME (the Settings checkbox, the tray toggle), and
        // a DI graph fixed at process start cannot swap NullPostProcessor for a
        // real LlamaPostProcessor later — so on any GPU machine the real
        // processor is always constructed, and `enabled: settings.CorrectVoseo`
        // is what decides ONLY whether it starts loaded.
        // DictationPipeline.StartAsync reads IPostProcessor.Enabled before firing
        // its own deferred ProbeAsync, so a machine that starts with correction
        // off never pays to load the ~2 GB model at launch either — see that
        // method's own comment.
        services.AddSingleton<IPostProcessor>(sp => acceleration == Acceleration.Gpu
            ? new LlamaPostProcessor(
                new LlamaEngine(
                    sp.GetRequiredService<PostProcessingOptions>(),
                    sp.GetRequiredService<ILogger<LlamaEngine>>()),
                sp.GetRequiredService<PostProcessingOptions>(),
                sp.GetRequiredService<ILogger<LlamaPostProcessor>>(),
                enabled: settings.CorrectVoseo)
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

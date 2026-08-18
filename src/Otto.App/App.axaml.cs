using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Otto.App.ViewModels;
using Otto.App.Views;
using Otto.Core;

namespace Otto.App;

public partial class App : Application
{
    private IServiceProvider services = null!;
    private TrayIcon? tray;
    private MainWindow? window;
    private CharacterWindow? character;
    private NativeMenuItem? characterItem;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public static IServiceProvider Services { get; set; } = null!;

    /// <summary>The clipboard belongs to a window, so the window has to be reachable.</summary>
    public static Window? Shell { get; private set; }

    public override void OnFrameworkInitializationCompleted()
    {
        services = Services;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Otto lives in the tray. Closing the last window is not a reason to
            // exit — that is the whole point of a background dictation tool.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            SetUpTray(desktop);
            SetUpCharacter();
            StartPipeline();

            // Launching something and having nothing happen on screen reads as
            // "it didn't work", even when it did. On a first run — or when the
            // tray icon could not be created — the window is the only proof Otto
            // is alive, so it opens.
            if (services.GetRequiredService<Settings>().IsFirstRun || tray is null)
                ShowWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetUpTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var pipeline = services.GetRequiredService<DictationPipeline>();

        try
        {
            tray = BuildTray(desktop, pipeline);
        }
        catch (Exception ex)
        {
            // Some shells refuse a tray icon. Otto still works — the hotkey is
            // registered either way — so this must not be fatal, but the user
            // needs the window instead of an invisible process.
            services.GetRequiredService<ILogger<App>>()
                .LogWarning(ex, "Could not create the tray icon; opening the window instead");
        }
    }

    private TrayIcon BuildTray(IClassicDesktopStyleApplicationLifetime desktop, DictationPipeline pipeline)
    {
        var tray = new TrayIcon
        {
            Icon = TrayIcons.For(pipeline.State),
            ToolTipText = "Otto",
            Menu = BuildMenu(desktop),
        };

        tray.Clicked += (_, _) => ShowWindow();

        pipeline.StateChanged += state => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            tray.Icon = TrayIcons.For(state);
            tray.ToolTipText = state switch
            {
                DictationState.Loading      => "Otto — cargando modelo",
                DictationState.Recording    => "Otto — escuchando",
                DictationState.Transcribing => "Otto — procesando",
                _ => "Otto — listo",
            };
        });

        TrayIcon.SetIcons(this, [tray]);

        return tray;
    }

    private NativeMenu BuildMenu(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var open = new NativeMenuItem("Abrir Otto");
        open.Click += (_, _) => ShowWindow();

        // The label says what clicking does, rather than a checkbox saying what
        // the state is. Tray menus render check marks inconsistently, and an
        // option whose two states look identical is worse than no option.
        characterItem = new NativeMenuItem();
        characterItem.Click += (_, _) => SetCharacterVisible(!CharacterVisible);

        var quit = new NativeMenuItem("Salir");
        quit.Click += (_, _) =>
        {
            services.GetRequiredService<DictationPipeline>().Dispose();
            desktop.Shutdown();
        };

        RefreshCharacterItem();

        return [open, characterItem, new NativeMenuItemSeparator(), quit];
    }

    private void ShowWindow()
    {
        if (window is null)
        {
            var view = services.GetRequiredService<MainViewModel>();
            view.UninstallRequested += RunUninstall;

            // The same switch lives in two places; this is the settings window
            // telling the tray what the user just chose.
            view.CharacterVisibilityChanged += visible => SetCharacterVisible(visible, persist: false);

            window = new MainWindow { DataContext = view };

            // Closing hides instead of destroying: reopening should be instant and
            // keep the scroll position and whatever the user was editing.
            window.Closing += (_, e) =>
            {
                e.Cancel = true;
                window.Hide();
            };

            Shell = window;
        }

        window.Show();
        window.Activate();
    }

    /// <summary>
    /// Two distributions, two ways out, and only one of them is Otto's job.
    ///
    /// Installed, Windows owns the removal — including asking whether the notes
    /// and the downloaded models should go — so all Otto does is start it and get
    /// out of the way. Portable, nothing else is going to clean up after a copied
    /// folder, so Otto does it itself.
    /// </summary>
    private void RunUninstall()
    {
        var log = services.GetRequiredService<ILogger<App>>();
        var installed = Uninstaller.InstalledUninstaller();

        // Launched before anything is torn down: if it cannot start, Otto stays
        // alive and working rather than shutting down into nothing.
        if (installed is not null && !Uninstaller.LaunchInstalled(installed, log)) return;

        // The pipeline is stopped before the delete so the database and the model
        // files are not held open while their folders are being removed —
        // otherwise the delete half-succeeds and the user is left with exactly
        // the mess they asked to avoid. The installed path needs it too, because
        // the uninstaller cannot remove files this process still has open.
        services.GetRequiredService<DictationPipeline>().Dispose();

        if (installed is null) Uninstaller.Run(log);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private bool CharacterVisible => character?.IsVisible == true;

    private void SetUpCharacter()
    {
        if (services.GetRequiredService<Settings>().ShowCharacter)
            SetCharacterVisible(true, persist: false);
    }

    /// <summary>
    /// Shows or hides the character, and remembers the choice.
    ///
    /// <para>
    /// The window is built the first time it is actually wanted rather than at
    /// startup: someone who runs with the character off never pays for it, and
    /// turning it on later is not a reason to restart.
    /// </para>
    /// <para>
    /// Hiding is <see cref="Window.Hide"/> and not <see cref="Window.Close"/>, so
    /// the native handle — and with it the click-through and never-focus styles
    /// applied when it opened — survives being turned off and on again.
    /// </para>
    /// </summary>
    private void SetCharacterVisible(bool visible, bool persist = true)
    {
        var log = services.GetRequiredService<ILogger<App>>();

        try
        {
            if (visible)
            {
                if (character is null)
                {
                    character = new CharacterWindow(services.GetRequiredService<IOverlayStyler>());
                    character.Follow(services.GetRequiredService<DictationPipeline>());
                }

                character.Show();
            }
            else
            {
                character?.Hide();
            }
        }
        catch (Exception ex)
        {
            // Personality is not a feature anyone should lose dictation over.
            character = null;
            log.LogWarning(ex, "Could not show the character; Otto keeps working just the same");
        }

        RefreshCharacterItem();

        if (!persist) return;

        // Amended rather than rebuilt, so toggling from the tray cannot reset a
        // setting this menu knows nothing about.
        var store = services.GetRequiredService<SettingsStore>();
        store.Save(store.Load() with { ShowCharacter = CharacterVisible });

        // The settings window offers the same switch. If it is already built, its
        // checkbox has to agree with what the tray just did.
        services.GetRequiredService<MainViewModel>().ReflectCharacterVisibility(CharacterVisible);
    }

    private void RefreshCharacterItem()
    {
        if (characterItem is not null)
            characterItem.Header = CharacterVisible ? "Esconder a Otto" : "Mostrar a Otto";
    }

    private void StartPipeline()
    {
        var pipeline = services.GetRequiredService<DictationPipeline>();
        var settings = services.GetRequiredService<Settings>();

        Autostart.RepairIfMoved();

        _ = pipeline.StartAsync(settings.ToBinding());
    }
}

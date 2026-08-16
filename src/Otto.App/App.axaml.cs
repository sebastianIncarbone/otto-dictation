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
                .LogWarning(ex, "No se pudo crear el ícono de bandeja; se abre la ventana en su lugar");
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

        var quit = new NativeMenuItem("Salir");
        quit.Click += (_, _) =>
        {
            services.GetRequiredService<DictationPipeline>().Dispose();
            desktop.Shutdown();
        };

        return [open, new NativeMenuItemSeparator(), quit];
    }

    private void ShowWindow()
    {
        if (window is null)
        {
            window = new MainWindow { DataContext = services.GetRequiredService<MainViewModel>() };

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

    private void StartPipeline()
    {
        var pipeline = services.GetRequiredService<DictationPipeline>();
        var settings = services.GetRequiredService<Settings>();

        Autostart.RepairIfMoved();

        _ = pipeline.StartAsync(settings.ToBinding());
    }
}

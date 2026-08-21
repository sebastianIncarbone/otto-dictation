using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Otto.App.ViewModels;
using Otto.App.Views;
using Otto.Core;
using Otto.Speech;

namespace Otto.App;

public partial class App : Application
{
    private IServiceProvider services = null!;
    private TrayIcon? tray;
    private MainWindow? window;
    private CharacterWindow? character;
    private NativeMenuItem? characterItem;
    private NativeMenuItem? ollamaItem;

    /// <summary>
    /// Which overlay to build. Held here rather than read from
    /// <see cref="Settings"/> at the point of use, because that instance is the
    /// snapshot taken at startup and goes stale the moment the user saves.
    /// </summary>
    private CharacterAppearance appearance = CharacterAppearance.Character;

    /// <summary>
    /// Set once, in <see cref="OnFrameworkInitializationCompleted"/>, and reused by
    /// Reintentar: <see cref="Progress{T}"/> only needs to be built on the UI
    /// thread once, and by the time a retry is possible the message loop is
    /// already pumping on that same thread.
    /// </summary>
    private IProgress<ProvisioningStatus>? provisioningProgress;

    /// <summary>
    /// Cancelled by the "Salir" tray handler, so quitting mid-download exits
    /// cleanly instead of racing the download to completion — the partial file
    /// is preserved either way, but there is no reason to make someone wait.
    /// </summary>
    private readonly CancellationTokenSource shutdown = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public static IServiceProvider Services { get; set; } = null!;

    /// <summary>The clipboard belongs to a window, so the window has to be reachable.</summary>
    public static Window? Shell { get; private set; }

    public override void OnFrameworkInitializationCompleted()
    {
        services = Services;

        // The platform is initialised by now, which is the earliest the UI thread's
        // dispatcher exists to hook. Everything below runs on that thread.
        Crash.InstallUiHandler(services.GetRequiredService<ILogger<App>>());

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Otto lives in the tray. Closing the last window is not a reason to
            // exit — that is the whole point of a background dictation tool.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            SetUpTray(desktop);
            SetUpCharacter();

            // Launching Otto while it is already running exits that second process
            // and lands here instead, which is the only way the launch has of
            // producing anything on screen. The knock arrives on a thread-pool
            // thread, so it is posted rather than run where it lands.
            services.GetRequiredService<SingleInstance>().Activated += () =>
                Avalonia.Threading.Dispatcher.UIThread.Post(ShowWindow);

            var needsProvisioning = services.GetRequiredService<ModelProvisioner>().NeedsProvisioning;

            // Progress<T> captures the ambient SynchronizationContext at
            // construction, not at the point it starts reporting — and this method
            // runs on the UI thread, so every report from here on arrives already
            // marshalled, with no Dispatcher.Post and no subscription to leak.
            // Built anywhere off the UI thread — inside ProvisionThenStartAsync,
            // say, after it has resumed past its own await — it would silently
            // post to the thread pool instead, and the window would never update.
            // Skipped entirely when nothing needs downloading: resolving
            // MainViewModel is itself a cost, and a normal launch must stay
            // exactly as cheap as it was before this feature existed.
            //
            // Done — and MainViewModel seeded via Apply() — BEFORE ShowWindow()
            // runs below, not after. MainViewModel is a singleton, so ShowWindow()
            // resolves this exact instance either way, but IsProvisioning defaults
            // to false and would otherwise only flip once the first Progress<T>
            // report is drained off the Avalonia dispatcher queue — which cannot
            // happen until this method returns and the message loop starts
            // pumping. Left in the old order, the very first paint could legally
            // show the notes view (search box, empty-notes message, Configuración)
            // for a frame before the provisioning card takes over, which is the
            // opposite of what the spec requires: those sections hidden before any
            // download progress is reported.
            if (needsProvisioning)
            {
                var view = services.GetRequiredService<MainViewModel>();
                view.Apply(new ProvisioningStatus(ProvisioningState.DownloadingSpeech));

                provisioningProgress = new Progress<ProvisioningStatus>(status =>
                {
                    view.Apply(status);
                    UpdateProvisioningTooltip(status);
                });
            }

            // Launching something and having nothing happen on screen reads as
            // "it didn't work", even when it did. On a first run, when models are
            // missing, or when the tray icon could not be created, the window is
            // the only proof Otto is alive, so it opens.
            if (needsProvisioning || services.GetRequiredService<Settings>().IsFirstRun || tray is null)
                ShowWindow();

            _ = ProvisionThenStartAsync(needsProvisioning, provisioningProgress);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The tray tooltip is the only feedback left once the window is closed mid-
    /// download. <see cref="ProvisioningState.Ready"/> is deliberately not handled
    /// here: <see cref="StartPipelineAsync"/> is about to run
    /// <see cref="DictationPipeline.StartAsync"/>, whose own <c>StateChanged</c>
    /// handler (in <see cref="BuildTray"/>) takes the tooltip back over from here.
    /// </summary>
    private void UpdateProvisioningTooltip(ProvisioningStatus status)
    {
        if (tray is null) return;

        switch (status.State)
        {
            case ProvisioningState.DownloadingSpeech:
                tray.ToolTipText = status.Progress is { } p
                    ? $"Otto — descargando {p.Fraction:P0}"
                    : "Otto — descargando el modelo";
                break;

            case ProvisioningState.PreparingVad:
                tray.ToolTipText = "Otto — preparando el detector de voz";
                break;

            case ProvisioningState.Failed:
                tray.ToolTipText = "Otto — no se pudo descargar el modelo";
                break;
        }
    }

    /// <summary>
    /// The one call site for <see cref="StartPipelineAsync"/>. Expressed as a single
    /// <c>await</c>-sequenced method rather than a callback so the ordering —
    /// provisioning finishes, then and only then the pipeline starts — cannot be
    /// interleaved by accident. <see cref="DictationPipeline"/> swallows exceptions
    /// by design, so calling <c>StartAsync</c> against a model that has not
    /// finished downloading would fail once and then never try again.
    /// </summary>
    private async Task ProvisionThenStartAsync(bool needsProvisioning, IProgress<ProvisioningStatus>? progress)
    {
        // Resolved before the try, not inside a catch: if this resolution itself threw,
        // the catch would throw too and the exception would go right back to being
        // unobserved by the caller, which discards this Task — the exact failure mode
        // the try/catch below exists to close.
        var logger = services.GetRequiredService<ILogger<App>>();

        // The caller discards this Task, so nothing observes what escapes here. Before
        // provisioning moved out of Program.cs, StartPipeline() was called directly and
        // a throw from it — Autostart.RepairIfMoved() reads the registry, which can fail
        // — propagated out of OnFrameworkInitializationCompleted and died loudly. Left
        // unguarded, that same throw would now be swallowed by an unobserved Task and
        // Otto would sit there with a tray icon, a window, and no hotkey registered.
        // Catching it here keeps the app alive, which is the invariant, but the log is
        // what stops "alive" from meaning "silently useless".
        try
        {
            if (!needsProvisioning)
            {
                await StartPipelineAsync();
                return;
            }

            var provisioner = services.GetRequiredService<ModelProvisioner>();
            var result = await provisioner.ProvisionAsync(progress, shutdown.Token);

            if (result == ProvisioningState.Ready) await StartPipelineAsync();
        }
        catch (HotkeyRegistrationException ex)
        {
            // The one failure this whole change exists to remove was invisible: a tray
            // icon, a window, and no hotkey, with nothing telling the user why. Caught
            // here — not inside DictationPipeline, which keeps swallowing everything
            // else by design, and not inside the adapter, which already knows the cause
            // but has no UI to speak through — and only here can StartAsync's failure
            // finally be awaited for real, instead of a fire-and-forget call whose
            // exception nobody was ever going to see.
            logger.LogError(ex,
                "Hotkey registration failed at startup: {Modifiers}+0x{Key:X2}, already in use: {AlreadyInUse}",
                ex.Binding.Modifiers, ex.Binding.VirtualKey, ex.AlreadyInUse);

            // A tray-only user would otherwise see nothing at all — the window is the
            // only proof anything is wrong, and the only place Configuración lives.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                services.GetRequiredService<MainViewModel>().ShowHotkeyFailure(ex.AlreadyInUse);
                ShowWindow();

                if (tray is not null) tray.ToolTipText = "Otto — no se pudo activar el atajo";
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Startup sequencing failed; Otto is running without a registered hotkey");
        }
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

            // The state's own tooltip from the first frame. A bare "Otto" said
            // nothing at exactly the moment the icon has no history to read it by.
            ToolTipText = StateShapes.Tooltip(pipeline.State),
            Menu = BuildMenu(desktop),
        };

        tray.Clicked += (_, _) => ShowWindow();

        pipeline.StateChanged += state => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            tray.Icon = TrayIcons.For(state);
            tray.ToolTipText = StateShapes.Tooltip(state);
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

        var bringCharacterToFront = new NativeMenuItem("Traer al frente");
        bringCharacterToFront.Click += (_, _) => BringCharacterToFront();

        // Re-probes on click regardless of the current state: cheap when Ollama is
        // already up, and the only way to recover without restarting Otto when it
        // was opened after Otto's own startup probe already ran and failed.
        var postProcessor = services.GetRequiredService<IPostProcessor>();
        ollamaItem = new NativeMenuItem();
        ollamaItem.Click += async (_, _) =>
        {
            await postProcessor.ProbeAsync();
            RefreshOllamaItem(postProcessor);
        };

        var quit = new NativeMenuItem("Salir");
        quit.Click += (_, _) =>
        {
            // Preserves the .part file exactly as well as letting the download
            // finish would — ModelDownloader never deletes it on cancellation
            // either — but there is no reason to make someone wait for it.
            shutdown.Cancel();

            services.GetRequiredService<DictationPipeline>().Dispose();
            desktop.Shutdown();
        };

        RefreshCharacterItem();
        RefreshOllamaItem(postProcessor);

        return [open, characterItem, bringCharacterToFront, ollamaItem, new NativeMenuItemSeparator(), quit];
    }

    /// <summary>
    /// Re-promotes the character overlay to the top of the "always on top" band.
    ///
    /// <c>Topmost</c> does not mean forever-above-every-other-topmost-window —
    /// Windows re-orders topmost windows among themselves as they are shown or
    /// promoted, so another app that goes topmost after the overlay opened (a call
    /// widget, a screen recorder, Task Manager's "always on top") can end up drawn
    /// above it. Toggling the property off and back on forces a fresh promotion.
    ///
    /// <see cref="Window.Activate"/> is not an option here the way it is for
    /// <see cref="ShowWindow"/>: the overlay must never take focus
    /// (<c>ShowActivated="False"</c> in CharacterWindow.axaml, and the click-through
    /// style <see cref="CharacterWindow.OnOpened"/> applies) — stealing focus right
    /// before an injection would send the dictation into Otto instead of the user's
    /// document. And this must never be what turns the character back on: a no-op
    /// when it is not currently shown, same as it staying hidden after "Esconder a
    /// Otto" until the user asks for it again.
    /// </summary>
    private void BringCharacterToFront()
    {
        if (character is not { IsVisible: true }) return;

        character.Topmost = false;
        character.Topmost = true;
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
            view.CharacterAppearanceChanged += SetCharacterAppearance;

            // Reintentar re-enters the same sequencing method retry went through
            // the first time, so success after a retry starts the pipeline through
            // the exact same path as any other launch.
            view.RetryRequested += () => _ = ProvisionThenStartAsync(true, provisioningProgress);

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

        // Restored before being shown. Show() on a minimised window leaves it
        // minimised, so without this the tray click — and the second launch that
        // now routes here — would look like nothing happened, which is the exact
        // failure this path exists to remove.
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;

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
        var settings = services.GetRequiredService<Settings>();

        appearance = settings.CharacterAppearance;

        if (settings.ShowCharacter)
            SetCharacterVisible(true, persist: false);
    }

    /// <summary>
    /// Switches between the character and the minimal glyph.
    ///
    /// Does not persist: the only thing that raises this is the settings window,
    /// which has already written the choice to disk as part of the same save. A
    /// second writer here would be the bounce <see cref="SetCharacterVisible"/>'s
    /// <c>persist</c> flag exists to prevent.
    ///
    /// Applied to the live window when there is one, so the change is visible
    /// immediately rather than at the next launch; when there is not, the field is
    /// enough, because the window reads it as it is built.
    /// </summary>
    private void SetCharacterAppearance(CharacterAppearance wanted)
    {
        appearance = wanted;

        character?.SetAppearance(wanted);
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
                    character = new CharacterWindow(services.GetRequiredService<IOverlayStyler>(), appearance);
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

    /// <summary>
    /// Posted rather than set directly: called both from the click handler (already
    /// on the UI thread) and from <see cref="StartPipelineAsync"/> right after the
    /// startup probe, whose await chain is not guaranteed to resume there — the same
    /// caution <see cref="BuildTray"/> takes with <c>StateChanged</c>.
    /// </summary>
    private void RefreshOllamaItem(IPostProcessor postProcessor) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ollamaItem is null) return;

            ollamaItem.Header = postProcessor.IsAvailable
                ? "Ollama: conectado ✓"
                : "Ollama: no conectado — reconectar";
        });

    /// <summary>
    /// Awaited now, not fired-and-forgotten: a registration failure has to reach
    /// <see cref="ProvisionThenStartAsync"/>'s try/catch, and a discarded Task would
    /// have taken the exception down with it instead.
    /// </summary>
    private async Task StartPipelineAsync()
    {
        var pipeline = services.GetRequiredService<DictationPipeline>();
        var settings = services.GetRequiredService<Settings>();

        Autostart.RepairIfMoved();

        await pipeline.StartAsync(settings.ToBinding());

        // StartAsync just ran the one-shot startup probe; the menu item was built
        // before it, so it still shows whatever IsAvailable defaulted to.
        RefreshOllamaItem(services.GetRequiredService<IPostProcessor>());
    }
}

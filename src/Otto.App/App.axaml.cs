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
    private NativeMenuItem? correctionItem;

    /// <summary>
    /// Whether a correction-model load attempt — the deferred one
    /// <see cref="DictationPipeline.StartAsync"/> kicks off, a manual
    /// "reintentar" click, or <see cref="SetCorrectionEnabled"/> turning
    /// correction back on — has settled at least once. Before the first one
    /// settles, <see cref="CorrectionTrayStates.For"/> cannot yet tell "still
    /// loading" from "failed", so the item reads as Loading instead of
    /// guessing. <see cref="SetCorrectionEnabled"/> clears this back to false
    /// right before starting a fresh enable, specifically so a live refresh
    /// from <see cref="IPostProcessor.AvailabilityChanged"/> landing mid-load
    /// cannot read a stale "settled" value left over from a PREVIOUS attempt
    /// and misreport a load that has not even started yet as Failed.
    /// </summary>
    private bool correctionLoadSettled;

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

            var provisioner = services.GetRequiredService<ModelProvisioner>();

            // IPostProcessor.Enabled, not services.GetRequiredService<Settings>().CorrectVoseo:
            // the Settings DI singleton is a startup snapshot that a runtime toggle
            // never refreshes (see MainViewModel.OfferKey's own comment on the exact
            // same trap), while IPostProcessor.Enabled is the live value by
            // construction. They agree at this exact point in startup — nothing has
            // had a chance to toggle anything yet — but reading the live one is what
            // keeps this call site correct if that ever changes.
            var needsProvisioning = provisioner.NeedsProvisioning(services.GetRequiredService<IPostProcessor>().Enabled);

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

                // The leg InitialProvisioningState reports, not always
                // DownloadingSpeech: an Ollama-era upgrade already has Whisper+VAD
                // on disk and starts on the correction leg instead, whose copy
                // must not claim "solo la primera vez" to someone who is not on a
                // first run. Seeding the wrong leg here would flash that wrong
                // copy for the one report cycle before ProvisionAsync's own first
                // Report() call corrects it.
                view.Apply(new ProvisioningStatus(provisioner.InitialProvisioningState));

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

            case ProvisioningState.DownloadingCorrection:
                tray.ToolTipText = status.Progress is { } cp
                    ? $"Otto — descargando corrección {cp.Fraction:P0}"
                    : "Otto — descargando el modelo de corrección";
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
            var result = await provisioner.ProvisionAsync(
                services.GetRequiredService<IPostProcessor>().Enabled, progress, shutdown.Token);

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

        var postProcessor = services.GetRequiredService<IPostProcessor>();
        var provisioningOptions = services.GetRequiredService<ProvisioningOptions>();

        // Hidden only on CPU-only hardware now — a 3B model can never load
        // inside the 2s dictation budget there (see ProvisioningOptions.HasGpu's
        // own doc comment, and CorrectionCoordinates, which already skips
        // downloading the GGUF on that hardware). There is nothing to report on
        // a corrector that was never asked to load and never could be: a click
        // that can never succeed is worse than no button at all.
        // CorrectionTrayStates.For enforces the same guarantee independently
        // (CorrectionTrayStateKind.Unsupported can never have CanRetry: true) —
        // this check is what keeps the item off the menu entirely rather than
        // showing that state.
        //
        // Settings.CorrectVoseo is deliberately NOT part of this check anymore:
        // the whole point of the runtime toggle is that the item has to stay on
        // the menu (as CorrectionTrayStateKind.Off) so a user who is currently
        // off can turn correction back on from here — see CorrectionTrayStates'
        // own doc comment on why Off exists.
        if (provisioningOptions.HasGpu)
        {
            // No "reconnect" here either — an in-process model has no
            // connection to lose. The click handler is a genuine two-way
            // toggle now, mirroring characterItem's own raw toggle above:
            // Off turns correction on (provisioning the GGUF first if it is
            // not there yet — CorrectVoseo can switch on at runtime even when
            // the model was never downloaded, see ProvisioningOptions.CorrectionCoordinates'
            // own doc comment), Ready/Loading turn it off, and Missing/Failed
            // keep their existing "reintentar" recovery untouched — the user
            // already wants correction on in those states, so clicking means
            // "try again", not "give up".
            correctionItem = new NativeMenuItem();
            correctionItem.Click += async (_, _) =>
            {
                var current = CorrectionTrayStates.For(
                    postProcessor.IsAvailable,
                    provisioningOptions.CorrectionPath is { } path && File.Exists(path),
                    correctionLoadSettled,
                    provisioningOptions.HasGpu,
                    postProcessor.Enabled,
                    postProcessor.IdleUnloaded);

                switch (current.Kind)
                {
                    case CorrectionTrayStateKind.Off:
                        SetCorrectionEnabled(true);
                        break;

                    case CorrectionTrayStateKind.Ready:
                    case CorrectionTrayStateKind.Loading:
                    case CorrectionTrayStateKind.Idle:
                        SetCorrectionEnabled(false);
                        break;

                    case CorrectionTrayStateKind.Missing:
                    case CorrectionTrayStateKind.Failed:
                        if (!current.CanRetry) break;

                        // Missing: the GGUF itself is not on disk.
                        // ModelProvisioner skips the speech/VAD legs (already
                        // present, or this item would not exist yet) and only
                        // attempts the correction download; a failed
                        // download is logged and swallowed inside
                        // ProvisionAsync itself, same as any other
                        // provisioning failure.
                        if (current.Kind == CorrectionTrayStateKind.Missing)
                        {
                            var provisioner = services.GetRequiredService<ModelProvisioner>();
                            await provisioner.ProvisionAsync(correctionEnabled: true, provisioningProgress, shutdown.Token);
                        }

                        // Failed: the file is there but the load did not
                        // succeed. LlamaPostProcessor.ProbeAsync is not
                        // sticky on failure — see its own doc comment — so
                        // calling it again is a real retry, not a no-op
                        // against a permanently stuck gate. Also reached
                        // right after a Missing-branch download succeeds,
                        // which is what actually loads the model for the
                        // first time.
                        await postProcessor.ProbeAsync();
                        correctionLoadSettled = true;

                        RefreshCorrectionItem(postProcessor, provisioningOptions);
                        break;

                    case CorrectionTrayStateKind.Unsupported:
                        break;
                }
            };

            // Phase 3 made the startup probe fire-and-forget (StartAsync no
            // longer awaits it), so RefreshCorrectionItem's call right after
            // StartPipelineAsync's await usually still shows the pre-load
            // state. CorrectionAvailabilityChanged is what DictationPipeline
            // fires once that deferred load actually settles — without this
            // subscription the item would read "cargando…" forever on
            // essentially every normal GPU + CorrectVoseo=true launch,
            // self-correcting only if the user happened to click the item
            // themselves. Startup-only and fires at most once per process —
            // correctionLoadSettled only needs to become true here, never
            // false again, which is exactly what makes a single non-removed
            // subscription the right shape for it.
            var pipeline = services.GetRequiredService<DictationPipeline>();
            pipeline.CorrectionAvailabilityChanged += () =>
            {
                correctionLoadSettled = true;
                RefreshCorrectionItem(postProcessor, provisioningOptions);
            };

            // Fixes the same class of staleness one layer further out: the
            // pipeline event above only ever covers the ONE startup load.
            // Everything that can happen to the corrector afterward — the
            // idle timer unloading it, the background reload that follows,
            // a manual tray/Settings toggle — changes IPostProcessor.IsAvailable/
            // Enabled/IdleUnloaded without going through DictationPipeline at
            // all, so nothing used to tell this menu item to catch up; it kept
            // showing whatever it last showed, including "Corrección: activa ✓"
            // for a model that had since been silently unloaded. postProcessor
            // itself now raises AvailabilityChanged for exactly those
            // transitions (see IPostProcessor's own doc comment on when it
            // does — and, just as deliberately, does not — fire), and
            // RefreshCorrectionItem already marshals to the UI thread on its
            // own, so this handler does not need to. Subscribed exactly once,
            // here, for the lifetime of the process — BuildMenu itself only
            // ever runs once (from SetUpTray, itself called once from
            // OnFrameworkInitializationCompleted), so there is no double-
            // subscribe or leak risk to guard against separately.
            postProcessor.AvailabilityChanged += () => RefreshCorrectionItem(postProcessor, provisioningOptions);

            RefreshCorrectionItem(postProcessor, provisioningOptions);
        }

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

        var menu = new NativeMenu { open, characterItem, bringCharacterToFront };

        if (correctionItem is not null) menu.Add(correctionItem);

        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quit);

        return menu;
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
            view.UpdateInstallStarted += RunUpdateInstall;

            // The same switch lives in two places; this is the settings window
            // telling the tray what the user just chose.
            view.CharacterVisibilityChanged += visible => SetCharacterVisible(visible, persist: false);
            view.CharacterAppearanceChanged += SetCharacterAppearance;

            // Same two-owner shape again: SaveSettings already wrote
            // CorrectVoseo/CorrectionIdleUnloadMinutes to disk as part of the
            // same save, so persist: false here for the same reason
            // CharacterVisibilityChanged's handler above does — a second
            // writer is how the character switch nearly became an infinite
            // bounce between its two owners, and this is the same switch
            // shape one field over.
            view.CorrectVoseoChanged += enabled => SetCorrectionEnabled(enabled, persist: false);
            view.CorrectionIdleUnloadMinutesChanged += minutes =>
                services.GetRequiredService<IPostProcessor>().SetIdleTimeout(
                    minutes > 0 ? TimeSpan.FromMinutes(minutes) : null);

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
    /// <summary>
    /// Gets out of the installer's way.
    ///
    /// <para>
    /// By the time this runs the downloaded installer is already started — the same
    /// ordering <see cref="RunUninstall"/> uses, and for the same reason: if it had
    /// failed to launch, Otto stays alive and working instead of shutting down into
    /// nothing. Here the ordering is load-bearing for a second reason too. Inno is
    /// about to overwrite the executable this process is running from, and it can
    /// only do that once the files are no longer held open.
    /// </para>
    /// <para>
    /// Disposing the pipeline is what releases them: the Whisper model, the SQLite
    /// connection and — through <c>IPostProcessor</c> — the correction model's
    /// native weights. Shutting down without it would leave the installer waiting
    /// on locks held by a process that is already on its way out, which is exactly
    /// the half-finished install <c>CloseApplications=yes</c> exists to avoid.
    /// </para>
    /// </summary>
    private void RunUpdateInstall()
    {
        services.GetRequiredService<ILogger<App>>().LogInformation(
            "An update is being installed; shutting down so the installer can replace the application");

        services.GetRequiredService<DictationPipeline>().Dispose();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

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
    /// Posted rather than set directly: called from the click handler (already on
    /// the UI thread), from <see cref="StartPipelineAsync"/> right after
    /// <c>StartAsync</c> returns, from <see cref="DictationPipeline.CorrectionAvailabilityChanged"/>
    /// once the deferred correction-model load it kicked off actually settles, and
    /// from <see cref="IPostProcessor.AvailabilityChanged"/> for every transition
    /// after that — an idle unload, the background reload that follows it, a
    /// manual toggle. None of those callers are guaranteed to already be on the
    /// UI thread; the idle timer and a background reload in particular fire from
    /// a timer callback and a detached Task, the same caution <see cref="BuildTray"/>
    /// takes with <c>StateChanged</c>.
    ///
    /// The decision itself — which state this is, and what its header says — is
    /// not made here. It lives in <see cref="CorrectionTrayStates.For"/>, the one
    /// part of this class that is actually unit tested; this method's own job is
    /// reduced to gathering the five observable inputs and writing the result
    /// into the native control.
    /// </summary>
    private void RefreshCorrectionItem(IPostProcessor postProcessor, ProvisioningOptions options) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (correctionItem is null) return;

            var modelFileExists = options.CorrectionPath is { } path && File.Exists(path);
            var state = CorrectionTrayStates.For(
                postProcessor.IsAvailable, modelFileExists, correctionLoadSettled, options.HasGpu,
                postProcessor.Enabled, postProcessor.IdleUnloaded);

            correctionItem.Header = state.Header;
        });

    /// <summary>
    /// Turns correction on or off — what the tray toggle and the Settings
    /// checkbox both call, mirroring <see cref="SetCharacterVisible"/>'s own
    /// two-owner shape exactly: the tray toggles and persists, the settings
    /// window raises an event and lets this method apply it without
    /// persisting again (<c>MainViewModel.SaveSettings</c> already wrote
    /// <c>CorrectVoseo</c> as part of the same save).
    ///
    /// Enabling may need to provision the GGUF first: <c>CorrectVoseo</c>
    /// can now switch on at runtime even when the model was never
    /// downloaded — started off, or an upgrade from an install that never
    /// fetched the correction leg — and <c>ProvisioningOptions</c> always
    /// carries real coordinates on GPU hardware now (see
    /// <c>ProvisioningOptions.CorrectionCoordinates</c>' own doc comment),
    /// so <c>ModelProvisioner.ProvisionAsync</c> always has something to
    /// fetch. A failed download degrades to raw text exactly like a failed
    /// load already does — never a crash.
    ///
    /// <c>async void</c> to match its two callers: a tray click handler and
    /// a view-model event, neither of which has anything to await this
    /// against. Both already accept that shape for the Missing/Failed retry
    /// path above, which awaits just as much before refreshing.
    ///
    /// <para>
    /// This method has no reentrancy guard, and round 2 made the tray item
    /// clickable in more states — Ready/Loading/Idle now route to a click
    /// same as Off does — so it CAN run several times concurrently: a
    /// previous call's own <c>await ... SetEnabledAsync</c> can still be
    /// queued behind <c>LlamaPostProcessor</c>'s gate when a new click
    /// starts a second call. That gate is a plain <c>SemaphoreSlim</c>,
    /// which gives no waiter-ordering guarantee, so N concurrent calls'
    /// awaits can resume in any order — NOT necessarily the order the
    /// clicks happened in. Persisting/reflecting this call's own closure-
    /// captured <paramref name="enabled"/> once its await resumes used to
    /// mean whichever call happened to resume LAST won the disk write, even
    /// if an intervening click had already asked for something else — the
    /// wrong value could then survive a relaunch. <see cref="CorrectionToggleCoordinator.ToggleAsync"/>
    /// is what closes that: it returns the LIVE <c>IPostProcessor.Enabled</c>
    /// once the toggle settles, not the value the call started with — and
    /// because that property only ever holds one value, every racing call's
    /// tail ends up persisting the identical, correct answer regardless of
    /// completion order. See its own doc comment for the full argument, and
    /// <c>CorrectionToggleCoordinatorTests</c> for the race pinned under
    /// test.
    /// </para>
    /// </summary>
    private async void SetCorrectionEnabled(bool enabled, bool persist = true)
    {
        var postProcessor = services.GetRequiredService<IPostProcessor>();
        var provisioningOptions = services.GetRequiredService<ProvisioningOptions>();

        // correctionLoadSettled otherwise stays permanently true from the very
        // first startup settle onward (nothing else ever clears it) — fine
        // right up until postProcessor.AvailabilityChanged starts refreshing
        // the item live (see the subscription in BuildMenu). Enabling flips
        // IPostProcessor.Enabled synchronously, inside SetEnabledAsync, BEFORE
        // its own ProbeAsync has even started — a live refresh landing at that
        // exact instant would otherwise read "the file is there, a load
        // settled a while ago, and it is still not available" and show
        // Failed ("no se pudo cargar — reintentar") for a load that has not
        // even begun yet. Cleared only for enabling: disabling always reads
        // as Off regardless of this flag (CorrectionTrayStates.For checks
        // correctVoseo ahead of deferredLoadSettled), so touching it there
        // would be a no-op dressed up as a fix.
        if (enabled) correctionLoadSettled = false;

        if (enabled && provisioningOptions.CorrectionPath is { } path && !File.Exists(path))
        {
            // provisioningProgress is only built eagerly in OnFrameworkInitializationCompleted
            // when SOMETHING needed provisioning at startup — on the ordinary
            // steady-state launch (nothing missing) it is still null here,
            // because resolving MainViewModel just to have a Progress<T> on
            // hand costs something on every normal launch, and this is the
            // one path that can reach a download without ever having gone
            // through that block. Built lazily instead, the first time it is
            // actually needed, and cached in the same field so a later call
            // — another toggle, Reintentar — reuses it. Safe to construct
            // here: like the eager path, this method only ever runs on the
            // UI thread (a tray click, or MainViewModel.SaveSettings via
            // CorrectVoseoChanged), which is what Progress<T> needs to
            // capture the right SynchronizationContext.
            provisioningProgress ??= new Progress<ProvisioningStatus>(status =>
            {
                services.GetRequiredService<MainViewModel>().Apply(status);
                UpdateProvisioningTooltip(status);
            });

            var provisioner = services.GetRequiredService<ModelProvisioner>();
            await provisioner.ProvisionAsync(correctionEnabled: true, provisioningProgress, shutdown.Token);
        }

        // settled, not enabled: see the doc comment above on why the tail
        // below must read the live IPostProcessor.Enabled once the toggle
        // resumes, rather than the enabled parameter this call started with.
        var settled = await CorrectionToggleCoordinator.ToggleAsync(postProcessor, enabled, shutdown.Token);
        correctionLoadSettled = true;

        RefreshCorrectionItem(postProcessor, provisioningOptions);

        if (!persist) return;

        // Amended rather than rebuilt, so toggling from the tray cannot reset a
        // setting this menu knows nothing about.
        var store = services.GetRequiredService<SettingsStore>();
        store.Save(store.Load() with { CorrectVoseo = settled });

        // The settings window offers the same switch. If it is already built, its
        // checkbox has to agree with what the tray just did.
        services.GetRequiredService<MainViewModel>().ReflectCorrectVoseo(settled);
    }

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

        StartReading(settings);

        // StartAsync only awaits the hotkey registration — the correction
        // model's own load runs deferred, in the background, and has
        // usually NOT settled by the time this line runs. This call still
        // reflects whatever IsAvailable currently is (normally still false
        // here), which is fine: the CorrectionAvailabilityChanged
        // subscription set up in BuildMenu is what refreshes the item again
        // once the deferred load actually finishes.
        RefreshCorrectionItem(
            services.GetRequiredService<IPostProcessor>(),
            services.GetRequiredService<ProvisioningOptions>());
    }

    /// <summary>
    /// Registers the reading hotkey, and only when the user has asked for reading.
    ///
    /// <para>
    /// Not registered when <see cref="Settings.ReadAloud"/> is off, unlike the dictation
    /// hotkey which is always taken: an unused global combination held by Otto is one
    /// another application cannot have, and a user who does not read aloud gets nothing
    /// in exchange for that.
    /// </para>
    /// <para>
    /// <b>A failure here is swallowed, and that is the opposite of how the dictation
    /// hotkey is treated.</b> That one is the product — a refused registration is
    /// surfaced to the user, because Otto with no dictation hotkey is Otto doing nothing
    /// at all, and the silence around exactly that failure is what
    /// <see cref="HotkeyRegistrationException"/> was added to end. Reading is optional,
    /// so it obeys the other rule Otto has always followed: everything optional degrades
    /// to nothing. Losing Ctrl+Alt+L to another application must not stop dictation from
    /// starting, and it must certainly not take the tray icon down with it.
    /// </para>
    /// </summary>
    private void StartReading(Settings settings)
    {
        if (!settings.ReadAloud) return;

        try
        {
            services.GetRequiredService<ReadingPipeline>().Register(settings.ToReadingBinding());
        }
        catch (Exception ex)
        {
            services.GetRequiredService<ILogger<App>>()
                .LogWarning(ex, "Could not register the reading hotkey; dictation continues without it");
        }
    }
}

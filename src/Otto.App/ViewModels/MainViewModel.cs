using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging.Abstractions;
using CommunityToolkit.Mvvm.Input;
using Otto.Core;
using Otto.Speech;

namespace Otto.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly INoteRepository repository;
    private readonly DictationPipeline pipeline;
    private readonly SettingsStore settingsStore;
    private readonly Func<IClipboard?> clipboard;
    private readonly string databasePath;
    private readonly ProvisioningOptions provisioningOptions;

    /// <summary>Fallback <see cref="ListeningLabel"/> reads while <c>pipeline.RegisteredHotkey</c> is still null (Loading).</summary>
    private readonly HotkeyBinding startupHotkey;

    /// <summary>
    /// The binding as it stands on disk — what the next launch will register. Distinct
    /// from <see cref="Captured"/>, which is only what the editor is holding: until
    /// <see cref="SaveSettings"/> runs, a capture is a proposal, not a promise.
    /// </summary>
    private HotkeyBinding savedHotkey;

    public MainViewModel(
        INoteRepository repository,
        DictationPipeline pipeline,
        SettingsStore settingsStore,
        Settings settings,
        string databasePath,
        Func<IClipboard?> clipboard,
        ProvisioningOptions provisioningOptions)
    {
        this.repository = repository;
        this.pipeline = pipeline;
        this.settingsStore = settingsStore;
        this.clipboard = clipboard;
        this.databasePath = databasePath;
        this.provisioningOptions = provisioningOptions;

        startupHotkey = settings.ToBinding();
        savedHotkey = startupHotkey;
        captured = startupHotkey;

        language = settings.Language;
        startWithWindows = settings.StartWithWindows;
        showCharacter = settings.ShowCharacter;
        checkForUpdates = settings.CheckForUpdates;

        pipeline.StateChanged += OnStateChanged;
        pipeline.Saved += OnSaved;

        state = pipeline.State;
    }

    public ObservableCollection<NoteViewModel> Notes { get; } = [];

    [ObservableProperty] private DictationState state;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private bool isSettingsOpen;

    [ObservableProperty] private string language;
    [ObservableProperty] private bool startWithWindows;
    [ObservableProperty] private bool showCharacter;

    public string StatusText => State switch
    {
        DictationState.Loading      => "Cargando modelo…",
        DictationState.Recording    => "Escuchando",
        DictationState.Transcribing => "Procesando",
        _ => $"Listo · {ListeningLabel}",
    };

    public bool IsEmpty => Notes.Count == 0;

    public string EmptyMessage => string.IsNullOrWhiteSpace(Search)
        ? $"Todavía no dictaste nada.\nMantené {ListeningLabel} en cualquier programa y hablá."
        : $"Nada que coincida con «{Search}».";

    /// <summary>StartAsync sets <c>pipeline.RegisteredHotkey</c> right before this fires, so everything derived from it renotifies here too.</summary>
    partial void OnStateChanged(DictationState value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ListeningLabel));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(HotkeyChangePending));
    }

    partial void OnSearchChanged(string value) => _ = ReloadAsync();

    public async Task ReloadAsync()
    {
        var notes = string.IsNullOrWhiteSpace(Search)
            ? await repository.RecentAsync(200)
            : await repository.SearchAsync(Search, 200);

        Notes.Clear();

        foreach (var note in notes)
            Notes.Add(Wrap(note));

        Refresh();
    }

    private NoteViewModel Wrap(Note note)
    {
        var view = new NoteViewModel(note, repository, clipboard);
        view.DeleteRequested += OnDeleteRequested;
        return view;
    }

    private async void OnDeleteRequested(NoteViewModel note)
    {
        await repository.DeleteAsync(note.Id);
        note.DeleteRequested -= OnDeleteRequested;

        Notes.Remove(note);
        Refresh();
    }

    /// <summary>
    /// A dictation lands on a background thread. Anything touching the collection
    /// the UI is bound to has to be marshalled back.
    /// </summary>
    private void OnSaved(Note note) => Dispatcher.UIThread.Post(() =>
    {
        if (!string.IsNullOrWhiteSpace(Search)) return;

        Notes.Insert(0, Wrap(note));
        Refresh();
    });

    private void Refresh()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    /// <summary>
    /// Raised when the user changes whether the character should be on screen, so
    /// whoever owns that window can act on it. The tray menu offers the same
    /// choice, and the two have to agree.
    /// </summary>
    public event Action<bool>? CharacterVisibilityChanged;

    // ---- Hotkey capture ----

    [ObservableProperty] private HotkeyBinding captured;
    [ObservableProperty] private bool isCapturingHotkey;
    [ObservableProperty] private string hotkeyHint = "";

    partial void OnCapturedChanged(HotkeyBinding value) => OnPropertyChanged(nameof(HotkeyLabel));

    /// <summary>
    /// Closing the settings card disarms an in-flight capture.
    ///
    /// Without this the armed state outlived the only UI that showed it, and the
    /// window-level tunnel handler marks every key handled while capture is on: typing
    /// anywhere in the window silently did nothing, and the next ordinary chord — a
    /// Ctrl+C or Ctrl+V meant for copy and paste — was committed as the new hotkey and
    /// persisted by the following Guardar. Hooked on the property rather than inside
    /// <c>ToggleSettings</c> so every writer of <c>IsSettingsOpen</c>, including
    /// <see cref="SaveSettings"/> and any future one, disarms it too.
    /// </summary>
    partial void OnIsSettingsOpenChanged(bool value)
    {
        if (!value) CancelHotkeyCapture();
    }

    /// <summary>The binding being edited — a pure function of <see cref="Captured"/>, never typed independently.</summary>
    public string HotkeyLabel => HotkeyLabels.For(Captured);

    /// <summary>
    /// What Otto is actually listening on: what <c>DictationPipeline</c> registered, or
    /// — while that is still null (Loading) — what Otto started up with. Read by
    /// <see cref="StatusText"/> and <see cref="EmptyMessage"/>, never by
    /// <see cref="HotkeyLabel"/>, so the edited value can never be mistaken for the one in effect.
    /// </summary>
    public string ListeningLabel => HotkeyLabels.For(pipeline.RegisteredHotkey ?? startupHotkey);

    /// <summary>
    /// Not a flag anyone sets — it IS "what will be in effect after the next launch
    /// differs from what is in effect now", so its lifetime is exactly the lifetime of
    /// the truth it states and the notice cannot become furniture nobody reads.
    ///
    /// Two details are load-bearing, and both were wrong first time round.
    ///
    /// It compares the <em>saved</em> binding, not <see cref="Captured"/>: capturing a
    /// combination changes nothing on disk, so announcing "applies on the next launch"
    /// before <c>Guardar</c> promises something that a restart then silently discards —
    /// which is the same lie this whole change exists to remove, wearing a new hat.
    ///
    /// And it falls back to <see cref="startupHotkey"/> exactly as
    /// <see cref="ListeningLabel"/> does. <c>RegisteredHotkey</c> is null for the whole
    /// of Loading, and a record compared against null is always unequal, so without the
    /// fallback the notice appeared on a launch where the user had changed nothing.
    /// </summary>
    public bool HotkeyChangePending => savedHotkey != (pipeline.RegisteredHotkey ?? startupHotkey);

    [RelayCommand]
    private void StartHotkeyCapture()
    {
        IsCapturingHotkey = true;
        HotkeyHint = "Presioná la combinación nueva…";
    }

    [RelayCommand]
    private void CancelHotkeyCapture()
    {
        IsCapturingHotkey = false;
        HotkeyHint = "";
    }

    /// <summary>
    /// The whole capture state machine, expressed only over <see cref="Otto.Core"/>
    /// types so <c>MainWindow.axaml.cs</c> stays a pure key-event translator in front of
    /// it. In order: not capturing → ignore; Escape → cancel, prior binding kept;
    /// <paramref name="virtualKey"/> == 0 (no Win32 code) → hint, stay open; the key is
    /// itself a modifier → live hint, stay open; no modifier held → refuse (a bare
    /// global key like "A" would steal that letter everywhere, including inside this
    /// capture UI); anything left over commits. The conflict probe against another
    /// application is Slice 3 (<c>IHotkeyAvailability</c>) — this slice commits directly.
    /// </summary>
    public void OfferKey(HotkeyModifiers modifiers, uint virtualKey)
    {
        if (!IsCapturingHotkey) return;

        if (virtualKey == 0x1B) // Escape
        {
            IsCapturingHotkey = false;
            HotkeyHint = "";
            return;
        }

        if (virtualKey == 0)
        {
            HotkeyHint = "Esa tecla no se reconoció. Probá con otra.";
            return;
        }

        if (HotkeyLabels.IsModifierKey(virtualKey))
        {
            var held = modifiers | HotkeyLabels.ImpliedModifier(virtualKey);
            HotkeyHint = $"{HotkeyLabels.ForModifiers(held)}+…";
            return;
        }

        if (modifiers == HotkeyModifiers.None)
        {
            HotkeyHint = "Agregá al menos Ctrl, Alt, Shift o Win antes de la tecla.";
            return;
        }

        Captured = new HotkeyBinding(modifiers, virtualKey);
        IsCapturingHotkey = false;
        HotkeyHint = "";
    }

    /// <summary>
    /// Lays this window's fields over the settings as they are on disk.
    ///
    /// Amending rather than rebuilding is the whole point. The window does not
    /// show every setting — the model names are not here — so constructing a fresh
    /// record would quietly reset every one it does not know about back to its
    /// default, and the tray menu writes to the same file.
    ///
    /// Kept separate from <see cref="SaveSettings"/>, and public, because saving
    /// also touches the registry and the disk; this part is the decision, and a
    /// decision should be checkable without side effects.
    /// </summary>
    public Settings ApplyTo(Settings stored) => stored with
    {
        Modifiers = Captured.Modifiers,
        VirtualKey = Captured.VirtualKey,
        HotkeyLabel = HotkeyLabel,
        Language = Language,
        StartWithWindows = StartWithWindows,
        ShowCharacter = ShowCharacter,
        CheckForUpdates = CheckForUpdates,
    };

    [RelayCommand]
    private void SaveSettings()
    {
        settingsStore.Save(ApplyTo(settingsStore.Load()));

        // Only now is the captured binding a fact on disk, so only now can the
        // "applies after restart" notice honestly speak about it.
        savedHotkey = Captured;
        OnPropertyChanged(nameof(HotkeyChangePending));

        Autostart.Apply(StartWithWindows);
        CharacterVisibilityChanged?.Invoke(ShowCharacter);

        IsSettingsOpen = false;
    }

    /// <summary>
    /// Reflects a change made somewhere else — the tray menu — without saving or
    /// raising anything back, which would bounce between the two owners forever.
    /// </summary>
    public void ReflectCharacterVisibility(bool visible) => ShowCharacter = visible;

    // ---- Updates ----

    [ObservableProperty] private bool checkForUpdates;
    [ObservableProperty] private string updateStatus = "";

    public string Version => UpdateChecker.Current;

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        UpdateStatus = "Buscando…";

        using var checker = new UpdateChecker(NullLogger<UpdateChecker>.Instance);
        var status = await checker.CheckAsync();

        UpdateStatus = status.Result switch
        {
            UpdateResult.Available => $"Hay una versión nueva: {status.LatestVersion}",
            UpdateResult.UpToDate  => $"Estás al día ({status.CurrentVersion})",
            _ => "No se pudo verificar. ¿Estás sin conexión?",
        };
    }

    // ---- Uninstall ----

    [ObservableProperty] private bool isConfirmingUninstall;
    [ObservableProperty] private string uninstallWarning = "";

    /// <summary>
    /// Two steps, not a dialog. The first click states exactly what disappears —
    /// how many notes, how many megabytes — because "borrar mis datos" is abstract
    /// and "borrar 340 notas" is not.
    ///
    /// What it promises depends on how Otto got here. Installed, Windows runs the
    /// removal and asks about the data itself, so claiming the notes are about to
    /// go would be describing something this button does not do.
    /// </summary>
    [RelayCommand]
    private void ConfirmUninstall()
    {
        var (bytes, notes) = Uninstaller.Summarise(databasePath);

        UninstallWarning = Uninstaller.InstalledUninstaller() is not null
            ? $"Se va a abrir el desinstalador de Windows, que te va a preguntar si querés " +
              $"borrar también tus {notes} nota(s) y los modelos descargados " +
              $"({bytes / 1024d / 1024:N0} MB). Otto se va a cerrar."
            : $"Se van a borrar {notes} nota(s), la configuración y los modelos descargados " +
              $"({bytes / 1024d / 1024:N0} MB). No se puede deshacer. Otto se va a cerrar.";

        IsConfirmingUninstall = true;
    }

    [RelayCommand]
    private void CancelUninstall()
    {
        IsConfirmingUninstall = false;
        UninstallWarning = "";
    }

    public event Action? UninstallRequested;

    [RelayCommand]
    private void Uninstall() => UninstallRequested?.Invoke();

    // ---- Provisioning ----

    /// <summary>
    /// While true, the window shows the provisioning card instead of the notes
    /// area — search, the list, and Configuración are all hidden. Stays true
    /// through <see cref="ProvisioningState.Failed"/> so the Reintentar button has
    /// somewhere to live; only <see cref="ProvisioningState.Ready"/> turns it off.
    /// </summary>
    [ObservableProperty] private bool isProvisioning;

    [ObservableProperty] private string provisioningText = "";
    [ObservableProperty] private string provisioningDetail = "";
    [ObservableProperty] private double provisioningPercent;
    [ObservableProperty] private bool hasProvisioningError;
    [ObservableProperty] private string provisioningError = "";

    /// <summary>
    /// The single entry point for every provisioning update. Kept as one method,
    /// rather than one handler per state, so the Rioplatense copy — and the
    /// decision about what <see cref="IsProvisioning"/> hides — lives in exactly
    /// one place.
    /// </summary>
    public void Apply(ProvisioningStatus status)
    {
        // An explicit case per reported state, rather than the shorter
        // `IsProvisioning = status.State != Ready`: ModelProvisioner never
        // reports Idle through this channel today — a cancelled provisioning
        // exits silently, with no final Report call — but "!= Ready" would
        // latch the card open on anything unexpected, with no error text and
        // no Reintentar: an unrecoverable dead end whose only way out is
        // restarting Otto. Idle and any future state both fall to the default
        // arm and degrade to "not provisioning" instead.
        switch (status.State)
        {
            case ProvisioningState.DownloadingSpeech:
                IsProvisioning = true;
                HasProvisioningError = false;
                ProvisioningText = $"Descargando {provisioningOptions.Label} ({provisioningOptions.Size}), solo la primera vez…";

                // No byte has moved yet on the first report for this leg — Progress
                // is null until the first chunk lands — so the reassurance sits
                // where the rate normally goes instead of showing 0 %.
                ProvisioningDetail = status.Progress is { } p
                    ? $"{p.Fraction:P0} · {p.BytesPerSecond / 1024 / 1024:N1} MB/s"
                    : "Si se corta, la próxima vez continúa desde donde quedó.";
                ProvisioningPercent = status.Progress?.Fraction ?? 0;
                break;

            case ProvisioningState.PreparingVad:
                // A separate, short step — not folded into the speech model's
                // percentage, because a bar that jumps back to 0 % after reaching
                // 100 % reads as a bug even when the two legs are unrelated.
                IsProvisioning = true;
                HasProvisioningError = false;
                ProvisioningText = "Preparando el detector de voz…";
                ProvisioningDetail = "";
                ProvisioningPercent = 0;
                break;

            case ProvisioningState.Failed:
                IsProvisioning = true;
                HasProvisioningError = true;
                ProvisioningError =
                    "No se pudo descargar el modelo. Fijate que tengas internet y probá de nuevo — lo que ya se bajó no se pierde.";
                break;

            case ProvisioningState.Ready:
            case ProvisioningState.Idle:
            default:
                IsProvisioning = false;
                HasProvisioningError = false;
                break;
        }
    }

    /// <summary>
    /// Raised when the user clicks Reintentar on a failed download. Follows the
    /// same event-not-call pattern as <see cref="UninstallRequested"/>: the view
    /// model does not own the provisioner, so it asks rather than acts.
    /// </summary>
    public event Action? RetryRequested;

    [RelayCommand]
    private void Retry() => RetryRequested?.Invoke();
}

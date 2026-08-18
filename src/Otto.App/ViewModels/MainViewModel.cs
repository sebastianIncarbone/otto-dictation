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

        hotkeyLabel = settings.HotkeyLabel;
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

    [ObservableProperty] private string hotkeyLabel;
    [ObservableProperty] private string language;
    [ObservableProperty] private bool startWithWindows;
    [ObservableProperty] private bool showCharacter;

    public string StatusText => State switch
    {
        DictationState.Loading      => "Cargando modelo…",
        DictationState.Recording    => "Escuchando",
        DictationState.Transcribing => "Procesando",
        _ => $"Listo · {HotkeyLabel}",
    };

    public bool IsEmpty => Notes.Count == 0;

    public string EmptyMessage => string.IsNullOrWhiteSpace(Search)
        ? $"Todavía no dictaste nada.\nMantené {HotkeyLabel} en cualquier programa y hablá."
        : $"Nada que coincida con «{Search}».";

    partial void OnStateChanged(DictationState value) => OnPropertyChanged(nameof(StatusText));

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

    /// <summary>
    /// Lays this window's fields over the settings as they are on disk.
    ///
    /// Amending rather than rebuilding is the whole point. The window does not
    /// show every setting — the hotkey binding and the model names are not here —
    /// so constructing a fresh record would quietly reset every one it does not
    /// know about back to its default, and the tray menu writes to the same file.
    ///
    /// Kept separate from <see cref="SaveSettings"/>, and public, because saving
    /// also touches the registry and the disk; this part is the decision, and a
    /// decision should be checkable without side effects.
    /// </summary>
    public Settings ApplyTo(Settings stored) => stored with
    {
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

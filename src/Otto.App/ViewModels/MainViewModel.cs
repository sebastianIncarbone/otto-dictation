using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging.Abstractions;
using CommunityToolkit.Mvvm.Input;
using Otto.Core;
using Otto.Speech;
using Otto.Tts;

namespace Otto.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly INoteRepository repository;
    private readonly DictationPipeline pipeline;
    private readonly SettingsStore settingsStore;
    private readonly Func<IClipboard?> clipboard;
    private readonly string databasePath;
    private readonly ProvisioningOptions provisioningOptions;
    private readonly IHotkeyAvailability hotkeyAvailability;
    private readonly Func<IStorageProvider?>? storage;
    private readonly Func<UpdateInstaller>? updateInstaller;

    /// <summary>Fallback <see cref="ListeningLabel"/> reads while <c>pipeline.RegisteredHotkey</c> is still null (Loading).</summary>
    private readonly HotkeyBinding startupHotkey;

    /// <summary>
    /// The binding as it stands on disk — what the next launch will register. Distinct
    /// from <see cref="Captured"/>, which is only what the editor is holding: until
    /// <see cref="SaveSettings"/> runs, a capture is a proposal, not a promise.
    /// </summary>
    private HotkeyBinding savedHotkey;

    /// <inheritdoc cref="savedHotkey"/>
    private HotkeyBinding savedReadingHotkey;

    /// <summary>
    /// <see cref="CorrectVoseo"/> as it was the last time it was actually
    /// acted on — either by <see cref="SaveSettings"/> raising
    /// <see cref="CorrectVoseoChanged"/>, or by <see cref="ReflectCorrectVoseo"/>
    /// recording a change the tray already applied on its own. Distinct from
    /// <see cref="savedHotkey"/>'s own "what is on disk" role: this tracks
    /// "what the live corrector was last told", which is what
    /// <see cref="CorrectVoseoChangedSinceApplied"/> needs to answer honestly.
    /// </summary>
    private bool lastAppliedCorrectVoseo;

    /// <inheritdoc cref="lastAppliedCorrectVoseo"/>
    private bool lastAppliedReadAloud;

    /// <summary>
    /// Null in every test, and Program.cs is the one place that passes it — the same
    /// optional-dependency shape as <see cref="storage"/> and <see cref="updateInstaller"/>.
    /// Without it the reading section reports no voice installed and offers no download,
    /// which is the honest rendering of "there is nothing here to install through" rather
    /// than a state the UI has to pretend cannot happen.
    /// </summary>
    private readonly VoiceInstaller? voiceInstaller;

    public MainViewModel(
        INoteRepository repository,
        DictationPipeline pipeline,
        SettingsStore settingsStore,
        Settings settings,
        string databasePath,
        Func<IClipboard?> clipboard,
        ProvisioningOptions provisioningOptions,
        IHotkeyAvailability hotkeyAvailability,

        // Resolved lazily and optional for the same reason the clipboard is: it
        // belongs to a window, and this view model is built before there is one.
        // Left out — as every test leaves it out — exporting finds nowhere to put a
        // file and declines, which is the honest answer when there is no window to
        // ask through. Program.cs is the one place that passes it.
        Func<IStorageProvider?>? storage = null,

        // A factory rather than an instance, because an UpdateInstaller owns an
        // HttpClient and this is used at most once in a session — the same reason
        // CheckUpdatesAsync builds its UpdateChecker inside a `using` instead of
        // holding one for the life of the window.
        //
        // Optional, and null in every test: with no factory the install button
        // never appears and the check behaves exactly as it did before this
        // existed. That is the honest degradation, not a hidden one — self-install
        // is already unavailable on the portable copy, so "no install offered" is
        // a state the UI has to render correctly regardless.
        Func<UpdateInstaller>? updateInstaller = null,

        // Optional and null in every test, like the two above. See the field's own
        // comment for what the reading section does without one.
        VoiceInstaller? voiceInstaller = null)
    {
        this.repository = repository;
        this.pipeline = pipeline;
        this.settingsStore = settingsStore;
        this.clipboard = clipboard;
        this.databasePath = databasePath;
        this.provisioningOptions = provisioningOptions;
        this.hotkeyAvailability = hotkeyAvailability;
        this.storage = storage;
        this.updateInstaller = updateInstaller;
        this.voiceInstaller = voiceInstaller;

        startupHotkey = settings.ToBinding();
        savedHotkey = startupHotkey;
        captured = startupHotkey;

        // No startupHotkey equivalent on purpose. Dictation always registers, so there is
        // always a "what Otto is listening on" to fall back to; reading may legitimately
        // never register at all (ReadAloud off), and inventing a fallback would have this
        // editor claim a binding is in effect when nothing is holding it.
        savedReadingHotkey = settings.ToReadingBinding();
        capturedReading = savedReadingHotkey;

        language = settings.Language;
        startWithWindows = settings.StartWithWindows;
        showCharacter = settings.ShowCharacter;
        appearance = settings.CharacterAppearance;
        checkForUpdates = settings.CheckForUpdates;
        correctVoseo = settings.CorrectVoseo;
        lastAppliedCorrectVoseo = settings.CorrectVoseo;
        correctionIdleUnloadMinutes = settings.CorrectionIdleUnloadMinutes;

        readAloud = settings.ReadAloud;
        lastAppliedReadAloud = settings.ReadAloud;

        // Resolved rather than looked up strictly: config.json is a file the user can edit
        // and an older Otto has already written, so an unrecognised id falls back to the
        // Argentine default instead of leaving this null and taking the window down.
        readingVoice = Voices.Resolve(settings.ReadingVoice);
        readingVoicing = PiperVoicing.Resolve(settings.ReadingVoicing);

        pipeline.StateChanged += OnPipelineStateChanged;
        pipeline.Saved += OnSaved;

        state = pipeline.State;
    }

    public ObservableCollection<NoteViewModel> Notes { get; } = [];

    [ObservableProperty] private DictationState state;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private bool isSettingsOpen;

    /// <summary>
    /// Whether the notes are the thing on screen right now.
    ///
    /// <para>
    /// The three things the body can be — the notes, the settings, the first
    /// download — are alternatives, not layers. Settings used to unfold above the
    /// list and leave the search field and Seleccionar peeking out underneath,
    /// which read as a panel that had dropped open over a screen still waiting
    /// behind it. It is a screen of its own, so it takes the whole space.
    /// </para>
    /// <para>
    /// Expressed here rather than as two conditions in the view because XAML has no
    /// "and": the alternative is a second copy of this rule written in a binding,
    /// where nothing can check it.
    /// </para>
    /// </summary>
    public bool IsShowingNotes => !IsProvisioning && !IsSettingsOpen;

    /// <summary>
    /// Whether the settings screen is the thing on screen right now — the same
    /// "alternatives, not layers" rule <see cref="IsShowingNotes"/> already
    /// states, applied to Ajustes instead of the notes list.
    ///
    /// <para>
    /// Before this existed, <c>MainWindow.axaml</c> bound the settings panel's
    /// visibility to <see cref="IsSettingsOpen"/> alone. That was safe only by
    /// accident: the one button that sets <see cref="IsSettingsOpen"/> to
    /// <see langword="true"/> is itself hidden while <see cref="IsProvisioning"/>
    /// is true, and nothing else ever flipped <see cref="IsProvisioning"/> after
    /// startup — so the two states could never actually be true at once. The
    /// tray's "reintentar" on a missing correction model broke that assumption:
    /// it can set <see cref="IsProvisioning"/> at an arbitrary later moment,
    /// with no relationship to whatever the window already had open, and Ajustes
    /// and the download card would render on top of each other.
    /// </para>
    /// <para>
    /// <see cref="IsSettingsOpen"/> itself is deliberately left untouched by a
    /// provisioning cycle — it stays the user's real intent rather than being
    /// reset to <see langword="false"/> and forgotten. That is what makes this a
    /// pure derived property instead of a third piece of state to keep in sync:
    /// once <see cref="IsProvisioning"/> goes back to <see langword="false"/>,
    /// this recomputes to <see langword="true"/> on its own and Ajustes
    /// reappears exactly where the user left it, with nothing destroyed and
    /// nothing to explicitly restore.
    /// </para>
    /// </summary>
    public bool IsShowingSettings => IsSettingsOpen && !IsProvisioning;

    [ObservableProperty] private string language;
    [ObservableProperty] private bool startWithWindows;
    [ObservableProperty] private bool showCharacter;

    /// <summary>
    /// The Configuración checkbox. Runtime-switchable now — SaveSettings
    /// raises <see cref="CorrectVoseoChanged"/> so <c>App</c> can load or
    /// unload the model immediately, the same two-owner shape
    /// <see cref="ShowCharacter"/>/<see cref="CharacterVisibilityChanged"/>
    /// already use.
    /// </summary>
    [ObservableProperty] private bool correctVoseo;

    /// <summary>
    /// Minutes of no correction before the model unloads to free VRAM; 0
    /// reads as "nunca" in Ajustes. See <c>Settings.CorrectionIdleUnloadMinutes</c>'s
    /// own doc comment for the default and the reasoning behind it.
    /// </summary>
    [ObservableProperty] private int correctionIdleUnloadMinutes;

    /// <summary>
    /// Whether Configuración should offer the correction section at all —
    /// mirrors the tray's own "hide what it can't do" treatment for
    /// CPU-only hardware (see <c>CorrectionTrayStates.Unsupported</c>): a
    /// checkbox for a feature that can never load inside the 2s dictation
    /// budget on this machine is worse than no checkbox at all.
    /// </summary>
    public bool ShowCorrectionSection => provisioningOptions.HasGpu;

    // ---- Lectura en voz alta ----

    /// <summary>
    /// The Configuración checkbox for reading the selection aloud.
    ///
    /// <para>
    /// No <c>ShowReadingSection</c> beside it, deliberately, and the absence is the
    /// interesting part: <see cref="ShowCorrectionSection"/> exists because a 3B model can
    /// never load inside the dictation budget on a CPU, so offering the checkbox there
    /// would be offering something that can never work. Piper runs on any CPU at x4,6, so
    /// reading has no such machine to hide from — it is the first optional thing Otto
    /// offers to everyone.
    /// </para>
    /// </summary>
    [ObservableProperty] private bool readAloud;

    public IReadOnlyList<Voice> ReadingVoices { get; } = Voices.All;

    public IReadOnlyList<PiperVoicing> ReadingVoicings { get; } = PiperVoicing.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadingVoiceInstalled))]
    [NotifyPropertyChangedFor(nameof(ReadingVoiceAvailability))]
    [NotifyPropertyChangedFor(nameof(CanDownloadVoice))]
    private Voice readingVoice;

    /// <summary>
    /// Which sampling preset reads — what is left of the "effort level" the feature was
    /// sketched with. Not a choice of model: the only Argentine voice exists at one
    /// quality tier, so a lighter model would cost the accent, and this product does not
    /// get to make that trade. See <c>PiperVoicing</c>.
    /// </summary>
    [ObservableProperty] private PiperVoicing readingVoicing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadVoice))]
    private bool isDownloadingVoice;

    [ObservableProperty] private double voiceDownloadFraction;

    /// <summary>What the reading section has to say about itself, or empty when it has nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadingStatus))]
    private string readingStatus = "";

    public bool HasReadingStatus => !string.IsNullOrWhiteSpace(ReadingStatus);

    /// <summary>
    /// Whether the picked voice is already on disk. False with no installer wired — every
    /// test leaves it out — which is the honest answer rather than a crash: with nothing
    /// to install through, no voice can be installed.
    /// </summary>
    public bool IsReadingVoiceInstalled => voiceInstaller?.IsInstalled(ReadingVoice) ?? false;

    public bool CanDownloadVoice => voiceInstaller is not null && !IsDownloadingVoice && !IsReadingVoiceInstalled;

    /// <summary>
    /// Whether the selected voice is already here, said in words instead of left to be
    /// inferred from a button appearing or not appearing.
    ///
    /// <para>
    /// Deliberately quotes no size. The catalogue stores none either — see
    /// <see cref="Voice.Label"/>'s comment on why a second copy of a fact only ever drifts
    /// from the first — and the true figure arrives as <c>Content-Length</c> when the
    /// download starts, which is where <see cref="ReadingStatus"/> already reports it. A
    /// number typed in here would be a third copy, stale the first time the voice is
    /// re-uploaded, and Otto has no way to check it without a network call before the
    /// click, which is exactly the thing the offline promise forbids.
    /// </para>
    /// <para>
    /// Empty with no installer at all: with nothing to install through there is no honest
    /// answer, and guessing "not installed" would offer a reassurance nothing can keep.
    /// </para>
    /// </summary>
    public string ReadingVoiceAvailability =>
        voiceInstaller is null
            ? ""
            : IsReadingVoiceInstalled
                ? "Ya está descargada en esta máquina."
                : "Todavía no está descargada. Se baja una sola vez, y después la lectura anda sin internet.";

    /// <summary>
    /// Downloading a voice is its own button, not something <c>Guardar</c> does quietly.
    ///
    /// <para>
    /// Otto's one rule about the network is that a person decides, not the application —
    /// it is why the update check is off by default and why installing an update takes two
    /// clicks rather than none. A ~110 MB transfer starting because somebody ticked a
    /// checkbox and pressed Guardar would be exactly the thing that rule exists to
    /// prevent, and "it is smaller than the update" is not a principle.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task DownloadVoiceAsync()
    {
        if (voiceInstaller is null || IsDownloadingVoice) return;

        var voice = ReadingVoice;

        IsDownloadingVoice = true;
        VoiceDownloadFraction = 0;
        ReadingStatus = $"Bajando la voz {voice.Label}…";

        // Marshalled to the UI thread by Progress<T> itself, captured here where the
        // context still exists — the same reason the provisioning progress is built in
        // App rather than inside ModelProvisioner.
        var progress = new Progress<VoiceDownloadProgress>(report =>
        {
            VoiceDownloadFraction = report.Fraction;

            ReadingStatus = report.Total > 0
                ? $"Bajando {voice.Label} — {report.Downloaded / 1048576:N0} de {report.Total / 1048576:N0} MB"
                : $"Bajando la voz {voice.Label}…";
        });

        try
        {
            await voiceInstaller.InstallAsync(voice, progress);

            ReadingStatus = $"La voz {voice.Label} quedó lista.";
            ReadingVoiceChanged?.Invoke(voice, ReadingVoicing);
        }
        catch (Exception ex)
        {
            // Same three states the update check insists on: "could not" is not "did not
            // need to". A failed download has to say so, because the alternative is a
            // checkbox that is on and a feature that silently never speaks.
            ReadingStatus = $"No se pudo bajar la voz: {ex.Message}";
        }
        finally
        {
            IsDownloadingVoice = false;
            OnPropertyChanged(nameof(IsReadingVoiceInstalled));
            OnPropertyChanged(nameof(ReadingVoiceAvailability));
            OnPropertyChanged(nameof(CanDownloadVoice));
        }
    }

    partial void OnReadingVoiceChanged(Voice value) => ReadingStatus = "";

    /// <summary>
    /// Raised on save, and only when <see cref="ReadAloud"/> actually changed since it was
    /// last applied — the same gate <see cref="CorrectVoseoChangedSinceApplied"/> exists
    /// for. <c>App</c> registers or releases the reading hotkey in response, and taking a
    /// global combination is not something to redo on every unrelated settings change.
    /// </summary>
    public event Action<bool>? ReadAloudChanged;

    /// <inheritdoc cref="ReadAloudChanged"/>
    public bool ReadAloudChangedSinceApplied => ReadAloud != lastAppliedReadAloud;

    /// <summary>
    /// Raised whenever the live synthesiser needs to be told which voice to use — on save,
    /// and again the moment a download finishes so the very next reading uses the voice
    /// that just arrived rather than waiting for another Guardar.
    /// </summary>
    public event Action<Voice, PiperVoicing>? ReadingVoiceChanged;

    /// <summary>
    /// Raised on save when the reading hotkey actually changed, so <c>App</c> can release
    /// the old combination and take the new one. Gated the same way
    /// <see cref="ReadAloudChanged"/> is, and for the same reason: re-registering a global
    /// hotkey that did not change is work that can only fail, never succeed differently.
    /// </summary>
    public event Action<HotkeyBinding>? ReadingHotkeyChanged;

    /// <summary>
    /// Which overlay is shown. Separate from <see cref="ShowCharacter"/>, which
    /// decides whether there is one at all.
    /// </summary>
    [ObservableProperty] private CharacterAppearance appearance;

    /// <summary>
    /// The three options of the "Apariencia" choice, as the booleans a radio group
    /// binds to.
    ///
    /// Written out rather than derived, because anything that let the options be
    /// set independently would allow the states that have no meaning — none
    /// checked, or more than one. Here the enum is the single value and these are
    /// views onto it, so neither can happen. Setting one to false does nothing:
    /// unchecking is what another one's check means.
    /// </summary>
    public bool IsCharacterAppearance
    {
        get => Appearance == CharacterAppearance.Character;
        set { if (value) Appearance = CharacterAppearance.Character; }
    }

    /// <inheritdoc cref="IsCharacterAppearance"/>
    public bool IsDiscreetAppearance
    {
        get => Appearance == CharacterAppearance.Discreet;
        set { if (value) Appearance = CharacterAppearance.Discreet; }
    }

    /// <inheritdoc cref="IsCharacterAppearance"/>
    public bool IsMinimalAppearance
    {
        get => Appearance == CharacterAppearance.Minimal;
        set { if (value) Appearance = CharacterAppearance.Minimal; }
    }

    /// <summary>
    /// Every option is raised, not just the one that became true: the radio the
    /// user did not click has to hear that it is now false, or the group shows two
    /// checked at once.
    /// </summary>
    partial void OnAppearanceChanged(CharacterAppearance value)
    {
        OnPropertyChanged(nameof(IsCharacterAppearance));
        OnPropertyChanged(nameof(IsDiscreetAppearance));
        OnPropertyChanged(nameof(IsMinimalAppearance));
    }

    /// <summary>
    /// A failed startup registration leaves <see cref="State"/> stuck at
    /// <see cref="DictationState.Loading"/> forever — nothing retries and nothing
    /// exits, because the hotkey <em>is</em> the dictation here. Consulted first so
    /// this header stops repeating "Cargando modelo…" about a load that already
    /// finished and quietly lying about it.
    /// </summary>
    public string StatusText => HasHotkeyAlert
        ? HotkeyAlertMessage
        : State switch
        {
            DictationState.Loading      => "Cargando modelo…",
            DictationState.Recording    => "Escuchando",
            DictationState.Transcribing => "Procesando",
            _ => $"Listo · {ListeningLabel}",
        };

    public bool IsEmpty => Notes.Count == 0;

    /// <summary>Whether the list on screen is a filtered one rather than all of it.</summary>
    public bool IsSearching => !string.IsNullOrWhiteSpace(Search);

    /// <summary>
    /// Whether the list is picking notes rather than reading them.
    ///
    /// A mode rather than a permanent row of checkboxes, because acting on several
    /// notes at once is the rare thing here and reading them is the common one.
    /// </summary>
    [ObservableProperty] private bool isSelecting;

    /// <summary>
    /// Pushed down to every note, and cleared on the way out. Each row binds to its
    /// own copy — the alternative is every row reaching up through the visual tree
    /// for the window's data context, which is the same fact expressed as a longer
    /// and more fragile binding.
    /// </summary>
    partial void OnIsSelectingChanged(bool value)
    {
        foreach (var note in Notes) note.IsSelecting = value;

        ExportError = "";
        RefreshSelection();
    }

    public int SelectedCount => Notes.Count(note => note.IsSelected);

    public bool HasSelection => SelectedCount > 0;

    public string SelectionLabel => SelectedCount == 1
        ? "1 nota seleccionada"
        : $"{SelectedCount} notas seleccionadas";

    /// <summary>The delete button names its own blast radius rather than just "Eliminar".</summary>
    public string DeleteSelectedLabel => $"Eliminar {SelectedCount}";

    /// <summary>
    /// The second step before a bulk delete.
    ///
    /// <para>
    /// Not in the design, which draws the button and nothing after it. Added
    /// because this repository already took a position on destructive actions that
    /// name a number — the uninstall asks twice, and the reason written next to it
    /// is that "borrar mis datos" is abstract and "borrar 340 notas" is not. One
    /// click that removes ninety dictations with no way back is the same case, and
    /// deleting one note from its own row is not: that one is a row somebody is
    /// looking straight at.
    /// </para>
    /// </summary>
    [ObservableProperty] private bool isConfirmingDeleteSelected;

    /// <inheritdoc cref="IsConfirmingDeleteSelected"/>
    public string DeleteSelectedWarning => SelectedCount == 1
        ? "Se borra 1 nota y no se puede recuperar."
        : $"Se borran {SelectedCount} notas y no se pueden recuperar.";

    /// <summary>
    /// Shown when a save could not be completed, and only then.
    ///
    /// The picker is its own confirmation on the way in — somebody chose the folder
    /// and the name — so success needs no announcement. A failure does: an export
    /// that quietly writes nothing leaves a person believing they have a copy.
    /// </summary>
    [ObservableProperty] private string exportError = "";

    /// <summary>
    /// Writes the picked notes to a file the person chooses.
    ///
    /// <para>
    /// The formatting lives in <see cref="NoteExport"/> and is tested there. What is
    /// here is the part that cannot be: a dialog, and a stream.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task ExportSelectedAsync(string? format)
    {
        ExportError = "";

        var picked = Notes.Where(note => note.IsSelected).ToList();

        if (picked.Count == 0 || storage?.Invoke() is not { } provider) return;

        var markdown = format == "md";

        try
        {
            var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Exportar notas",
                SuggestedFileName = NoteExport.SuggestedName(DateTimeOffset.Now, markdown),
                DefaultExtension = markdown ? "md" : "txt",
            });

            // Cancelling the dialog is an answer, not a failure. Nothing to report
            // and nothing to clean up.
            if (file is null) return;

            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream, NoteExport.Encoding);

            await writer.WriteAsync(markdown
                ? NoteExport.ToMarkdown(picked)
                : NoteExport.ToPlainText(picked));
        }
        catch (Exception)
        {
            // A full disk, a folder that turned read-only, a drive pulled out
            // mid-save. Otto keeps running — losing an export is not losing a note —
            // but the person has to know the copy they think they made is not there.
            ExportError = "No se pudo exportar.";
            return;
        }

        IsSelecting = false;
    }

    /// <inheritdoc cref="IsConfirmingDeleteSelected"/>
    [RelayCommand]
    private void ConfirmDeleteSelected() => IsConfirmingDeleteSelected = true;

    /// <inheritdoc cref="IsConfirmingDeleteSelected"/>
    [RelayCommand]
    private void CancelDeleteSelected() => IsConfirmingDeleteSelected = false;

    private void RefreshSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionLabel));
        OnPropertyChanged(nameof(DeleteSelectedLabel));
        OnPropertyChanged(nameof(DeleteSelectedWarning));

        // Changing the set invalidates the question: "se borran 3 notas" was asked
        // about three particular notes, and answering yes to it after a fourth was
        // ticked would be answering something nobody was asked.
        IsConfirmingDeleteSelected = false;
    }

    /// <summary>
    /// Enters selection mode, closing whatever was open first. An editor left
    /// underneath would be a row in two states at once, and its Guardar would sit
    /// among a set of buttons that act on entirely different notes.
    /// </summary>
    [RelayCommand]
    private void StartSelecting()
    {
        foreach (var note in Notes) note.CloseEditor();

        IsSelecting = true;
    }

    [RelayCommand]
    private void CancelSelecting() => IsSelecting = false;

    /// <summary>
    /// Everything currently listed, which during a search is the search's result
    /// and not the whole database. "Todas" has to mean the same set the person can
    /// see, or the count under it describes notes that are not on screen.
    /// </summary>
    [RelayCommand]
    private void SelectAll()
    {
        foreach (var note in Notes) note.IsSelected = true;
    }

    /// <summary>
    /// Deletes the picked notes, one repository call each.
    ///
    /// The collection is copied first: removing from <see cref="Notes"/> while
    /// walking it is the classic way to skip every second item, and here the items
    /// being skipped are the ones somebody asked to delete.
    /// </summary>
    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        foreach (var note in Notes.Where(note => note.IsSelected).ToList())
        {
            await repository.DeleteAsync(note.Id);

            Forget(note);
            Notes.Remove(note);
        }

        IsConfirmingDeleteSelected = false;
        IsSelecting = false;
        Refresh();
    }

    /// <summary>
    /// How many notes the search found, shown above them.
    ///
    /// A filtered list looks exactly like a short one, and without this the only
    /// way to tell "your search found three" from "you have three notes" is to
    /// remember what you typed.
    /// </summary>
    public string ResultCount => Notes.Count == 1 ? "1 NOTA" : $"{Notes.Count} NOTAS";

    /// <summary>
    /// Same alert rule as <see cref="StatusText"/>, but a search wins over it.
    ///
    /// <para>
    /// The order matters and the obvious one was wrong. This slot describes the list,
    /// and while a search is active the only thing that explains an empty list is that
    /// the search matched nothing — putting the hotkey alert here instead leaves the
    /// user with no idea their query came up empty, and repeats a sentence already on
    /// screen in the header two lines up.
    /// </para>
    ///
    /// <para>
    /// With no search, the alert does belong here: the default text tells the user to
    /// hold a hotkey that failed to register, and an instruction they cannot follow is
    /// worse than none.
    /// </para>
    /// </summary>
    public string EmptyHeading => IsSearching
        ? $"Nada que coincida con «{Search}»."
        : HasHotkeyAlert
            ? HotkeyAlertMessage
            : "Todavía no dictaste nada.";

    /// <summary>
    /// Whether the empty list is the one that means "you have not started yet",
    /// rather than a search that found nothing or a hotkey that failed.
    ///
    /// It is the only one of the three the design illustrates, and the only one
    /// that gets a second line. Telling somebody to hold a hotkey is an invitation,
    /// and the other two are not moments to be invited into anything.
    /// </summary>
    public bool HasNoDictationsYet => !IsSearching && !HasHotkeyAlert;

    /// <inheritdoc cref="HasNoDictationsYet"/>
    public string EmptyDetail => HasNoDictationsYet
        ? $"Mantené {ListeningLabel} en cualquier programa y hablá."
        : string.Empty;

    /// <summary>
    /// The empty slot's three parts move together — each of them reads
    /// <see cref="Search"/>, <see cref="HasHotkeyAlert"/> or
    /// <see cref="ListeningLabel"/> — so they are renotified together. Raised one
    /// at a time across five call sites, the one that gets forgotten is a slot
    /// left showing the previous state, which is the failure this whole
    /// arrangement is built to avoid.
    /// </summary>
    private void RefreshEmpty()
    {
        OnPropertyChanged(nameof(EmptyHeading));
        OnPropertyChanged(nameof(EmptyDetail));
        OnPropertyChanged(nameof(HasNoDictationsYet));
    }

    /// <summary>
    /// <c>DictationPipeline</c> fires <c>StateChanged</c> from whatever thread the
    /// transition happened on — the <c>Otto.Hotkey</c> pump thread on press/release, a
    /// thread-pool thread once transcription finishes — never the UI thread.
    /// <see cref="OnSaved"/> already established the fix for exactly this shape of
    /// problem: post to the dispatcher rather than touching bound state directly.
    ///
    /// <para>
    /// Setting <see cref="State"/> here, instead of subscribing this method to
    /// <c>StateChanged</c> directly, is load-bearing. This method's name would
    /// otherwise collide with the CommunityToolkit-generated <see cref="OnStateChanged"/>
    /// partial hook below, and wiring the pipeline event straight to that hook — which
    /// is what this class used to do — meant it ran without ever going through the
    /// generated <see cref="State"/> property setter, so the backing field was never
    /// actually written. <see cref="State"/> stayed frozen at whatever the pipeline
    /// reported at construction time, which on a first-run or provisioning launch is
    /// <see cref="DictationState.Loading"/> for the rest of the session.
    /// </para>
    /// </summary>
    private void OnPipelineStateChanged(DictationState value) => Dispatcher.UIThread.Post(() => State = value);

    /// <summary>
    /// The CommunityToolkit-generated hook for <see cref="State"/>. It runs inside the
    /// generated property setter, right after the backing field is written, so this
    /// body is already on the UI thread thanks to <see cref="OnPipelineStateChanged"/>
    /// above. <c>pipeline.RegisteredHotkey</c> is set immediately before <c>StartAsync</c>
    /// transitions to <see cref="DictationState.Idle"/>, so everything derived from it
    /// renotifies here too.
    /// </summary>
    partial void OnStateChanged(DictationState value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ListeningLabel));
        RefreshEmpty();
        OnPropertyChanged(nameof(HotkeyChangePending));
        OnPropertyChanged(nameof(IsListening));
        OnPropertyChanged(nameof(IsWorking));
    }

    /// <summary>
    /// The status line's emphasis, as the booleans a style selector binds to.
    ///
    /// The redesign gives the two active states their own colour and a heavier
    /// weight, and leaves the two quiet ones muted — attention belongs to the
    /// moments when Otto is doing something with your voice, not to the hours it
    /// spends waiting.
    ///
    /// Both go false under <see cref="HasHotkeyAlert"/>. That failure leaves
    /// <see cref="State"/> stuck at <see cref="DictationState.Loading"/> forever, so
    /// neither would fire anyway — but stating it here means the alert's own styling
    /// cannot be fought over by a state that is no longer moving.
    /// </summary>
    public bool IsListening => !HasHotkeyAlert && State == DictationState.Recording;

    /// <inheritdoc cref="IsListening"/>
    public bool IsWorking => !HasHotkeyAlert && State == DictationState.Transcribing;

    /// <summary>
    /// <see cref="EmptyHeading"/> reads <see cref="Search"/> directly, but nothing
    /// generated for <see cref="Search"/> notifies it. Raising it here — rather
    /// than relying only on <see cref="Refresh"/> at the tail of the fire-and-forget
    /// <see cref="ReloadAsync"/> — keeps it accurate even when the repository call
    /// inside <see cref="ReloadAsync"/> throws and <see cref="Refresh"/> is never
    /// reached, which used to leave the slot showing the previous search's text.
    /// </summary>
    partial void OnSearchChanged(string value)
    {
        RefreshEmpty();
        OnPropertyChanged(nameof(IsSearching));
        _ = ReloadAsync();
    }

    /// <summary>
    /// The way back out of a search that found nothing. Clearing the box by hand
    /// is not hard, but it is the only thing left to do at that point, and the
    /// screen may as well say so.
    /// </summary>
    [RelayCommand]
    private void ClearSearch() => Search = string.Empty;

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

    /// <summary>
    /// The query is stamped on at construction rather than bound. A note's rows
    /// are rebuilt on every search — <see cref="OnSearchChanged"/> reloads — so the
    /// value can never go stale, and a live binding would only give every note on
    /// screen something else to listen to.
    /// </summary>
    private NoteViewModel Wrap(Note note)
    {
        var view = new NoteViewModel(note, repository, clipboard)
        {
            Query = Search,

            // A note arriving mid-selection — a dictation saved while the list is
            // being picked through — joins the mode rather than showing up as the
            // one row without a checkbox.
            IsSelecting = IsSelecting,
        };

        view.DeleteRequested += OnDeleteRequested;
        view.EditStarted += OnEditStarted;
        view.SelectionChanged += RefreshSelection;
        return view;
    }

    /// <summary>
    /// Unsubscribes everything <see cref="Wrap"/> attached. One place, because a
    /// handler dropped from one of the two removal paths and not the other is a
    /// note that keeps answering after it is gone.
    /// </summary>
    private void Forget(NoteViewModel note)
    {
        note.DeleteRequested -= OnDeleteRequested;
        note.EditStarted -= OnEditStarted;
        note.SelectionChanged -= RefreshSelection;
    }

    /// <summary>
    /// One note is being edited, so the others go back to being read.
    ///
    /// <para>
    /// The list is the owner of this rule because no single note can be: each one
    /// only knows about itself. <see cref="NoteViewModel.CloseEditor"/> declines
    /// when it has unsaved changes, so opening a second note leaves the first one
    /// open rather than discarding what was typed into it.
    /// </para>
    /// </summary>
    private void OnEditStarted(NoteViewModel opened)
    {
        foreach (var note in Notes)
            if (!ReferenceEquals(note, opened))
                note.CloseEditor();
    }

    private async void OnDeleteRequested(NoteViewModel note)
    {
        await repository.DeleteAsync(note.Id);

        Forget(note);
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
        RefreshEmpty();
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(ResultCount));
        RefreshSelection();
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
    /// Raised on save so <c>App</c> can swap the live overlay. Carries no
    /// obligation to persist — <see cref="SaveSettings"/> has already done that,
    /// and a second writer is how the character switch nearly became an infinite
    /// bounce between its two owners.
    /// </summary>
    public event Action<CharacterAppearance>? CharacterAppearanceChanged;

    /// <summary>
    /// Raised on save — but only when <see cref="CorrectVoseo"/> actually
    /// changed since it was last applied, see
    /// <see cref="CorrectVoseoChangedSinceApplied"/> — so <c>App</c> can load
    /// or unload the live corrector. Carries no obligation to persist —
    /// <see cref="SaveSettings"/> has already done that — same shape as
    /// <see cref="CharacterVisibilityChanged"/>.
    /// </summary>
    public event Action<bool>? CorrectVoseoChanged;

    /// <summary>
    /// Whether <see cref="SaveSettings"/> should raise <see cref="CorrectVoseoChanged"/>.
    /// Kept separate and public for the same reason <see cref="ApplyTo"/> is:
    /// <c>App.SetCorrectionEnabled</c> attempts to provision the ~2 GB
    /// correction GGUF whenever it is asked to turn correction on and the
    /// file is missing, with no guard of its own for "already on" — so
    /// firing this event on every save, whether or not the checkbox was
    /// touched, retried that download on every unrelated settings change for
    /// as long as a previous attempt had failed. This is the decision, and a
    /// decision should be checkable without touching the registry or the
    /// disk the way <see cref="SaveSettings"/> itself does.
    /// </summary>
    public bool CorrectVoseoChangedSinceApplied => CorrectVoseo != lastAppliedCorrectVoseo;

    /// <inheritdoc cref="CorrectVoseoChanged"/>
    public event Action<int>? CorrectionIdleUnloadMinutesChanged;

    // ---- Hotkey capture ----

    [ObservableProperty] private HotkeyBinding captured;
    [ObservableProperty] private HotkeyBinding capturedReading;
    [ObservableProperty] private bool isCapturingHotkey;
    [ObservableProperty] private string hotkeyHint = "";

    /// <summary>
    /// Which of Otto's two hotkeys the one capture machine is editing.
    ///
    /// <para>
    /// One machine with a target, not two machines. The window-level tunnel handler marks
    /// every key handled while <see cref="IsCapturingHotkey"/> is on — see
    /// <see cref="OnIsSettingsOpenChanged"/> for the bug that came out of that state
    /// outliving its UI — and two independently armed captures could both be on at once,
    /// with the first key press committed to whichever one <c>OfferKey</c> happened to ask
    /// about first. There is one armed state because there can only be one.
    /// </para>
    /// </summary>
    private HotkeyTarget capturingTarget;

    partial void OnCapturedChanged(HotkeyBinding value) => OnPropertyChanged(nameof(HotkeyLabel));

    partial void OnCapturedReadingChanged(HotkeyBinding value) => OnPropertyChanged(nameof(ReadingHotkeyLabel));

    /// <summary>
    /// Both editors are collapsed by the same flag, so each needs to know whether the
    /// armed capture is <em>its</em> capture — otherwise arming the reading editor would
    /// also swap the dictation row into its "presioná la combinación" state.
    /// </summary>
    partial void OnIsCapturingHotkeyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCapturingDictationHotkey));
        OnPropertyChanged(nameof(IsCapturingReadingHotkey));
    }

    public bool IsCapturingDictationHotkey => IsCapturingHotkey && capturingTarget == HotkeyTarget.Dictation;

    public bool IsCapturingReadingHotkey => IsCapturingHotkey && capturingTarget == HotkeyTarget.Reading;

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

        OnPropertyChanged(nameof(IsShowingNotes));
        OnPropertyChanged(nameof(IsShowingSettings));
    }

    /// <summary>The binding being edited — a pure function of <see cref="Captured"/>, never typed independently.</summary>
    public string HotkeyLabel => HotkeyLabels.For(Captured);

    /// <inheritdoc cref="HotkeyLabel"/>
    public string ReadingHotkeyLabel => HotkeyLabels.For(CapturedReading);

    /// <summary>
    /// What Otto is actually listening on: what <c>DictationPipeline</c> registered, or
    /// — while that is still null (Loading) — what Otto started up with. Read by
    /// <see cref="StatusText"/> and <see cref="EmptyHeading"/>, never by
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
    private void StartHotkeyCapture() => StartCapture(HotkeyTarget.Dictation);

    [RelayCommand]
    private void StartReadingHotkeyCapture() => StartCapture(HotkeyTarget.Reading);

    private void StartCapture(HotkeyTarget target)
    {
        // Set before the flag, not after: IsCapturingHotkey's own handler is what
        // republishes the two derived flags, and it reads this.
        capturingTarget = target;

        IsCapturingHotkey = true;
        HotkeyHint = "Presioná la combinación nueva…";
    }

    [RelayCommand]
    private void CancelHotkeyCapture()
    {
        IsCapturingHotkey = false;
        HotkeyHint = "";

        // A capture that committed a candidate (OfferKey's last branch) but was then
        // abandoned — the settings card closed, or the standalone Cancelar command
        // called directly — left Captured holding that candidate. ApplyTo writes
        // Captured unconditionally, so the next unrelated Guardar (a language change,
        // an autostart toggle) would have persisted a hotkey the user never asked to
        // save. Restoring to savedHotkey — what is actually on disk — is always safe
        // here even when nothing was ever committed, since Captured then already
        // equals savedHotkey and this is a no-op.
        //
        // Both are restored regardless of which one was being edited: the abandoned
        // candidate is only ever in one of them, and restoring the untouched one is the
        // same no-op this comment already describes.
        Captured = savedHotkey;
        CapturedReading = savedReadingHotkey;
    }

    /// <summary>
    /// The whole capture state machine, expressed only over <see cref="Otto.Core"/>
    /// types so <c>MainWindow.axaml.cs</c> stays a pure key-event translator in front of
    /// it. In order: not capturing → ignore; Escape → cancel, prior binding kept;
    /// <paramref name="virtualKey"/> == 0 (no Win32 code) → hint, stay open; the key is
    /// itself a modifier → live hint, stay open; no modifier held → refuse (a bare
    /// global key like "A" would steal that letter everywhere, including inside this
    /// capture UI); anything left over is probed against <see cref="IHotkeyAvailability"/>
    /// and refused if another application already owns it, then commits.
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

        var candidate = new HotkeyBinding(modifiers, virtualKey);

        // Otto's other hotkey is the one collision IHotkeyAvailability cannot report
        // honestly: it answers "taken" for a combination Otto itself holds, and the
        // self-conflict widening below then forgives that — correctly for the binding
        // being edited, wrongly for the other one. Without this check, putting reading on
        // dictation's combination is accepted here and refused by Windows at the next
        // launch, where App.SetReadingEnabled swallows the refusal by design: the user
        // ends up with a key that does nothing and no explanation anywhere.
        var other = capturingTarget == HotkeyTarget.Reading ? Captured : CapturedReading;

        if (candidate == other)
        {
            var already = capturingTarget == HotkeyTarget.Reading ? "dictar" : "leer";
            HotkeyHint = $"{HotkeyLabels.For(candidate)} ya lo usa Otto para {already}. Probá otra combinación.";
            return;
        }

        // Compared against what Otto actually registered at startup, never the
        // stored setting — that would be accidentally right only because the DI
        // Settings singleton is never refreshed, and wrong after a failed
        // registration, exactly when the probe most needs to tell the truth.
        // Refused outright rather than committed with a warning: a combination
        // another app already holds will fail at Otto's next launch regardless.
        //
        // RegisteredHotkey is also null for the whole model-load window, before
        // StartAsync has attempted Register at all — and startupHotkey is exactly
        // the binding it is about to register. Probing that here would race
        // Otto's own pending registration: if the two RegisterHotKey calls land
        // close together, one of them loses and Otto reports "another
        // application" for a collision it caused itself. So self-conflict is
        // widened to startupHotkey too, but ONLY while still loading — HasHotkeyAlert
        // is what tells that window apart from a real registration failure, where
        // RegisteredHotkey is null for a different reason and startupHotkey is
        // precisely the binding that just failed. Skipping the probe there would
        // hide a real conflict from the user at the exact moment they need to see
        // it, which is why the fallback never applies once an alert is showing.
        var stillLoading = pipeline.RegisteredHotkey is null && !HasHotkeyAlert;

        // Reading's self-conflict is measured against what is stored, not against a
        // RegisteredHotkey: ReadingPipeline is deliberately not a dependency of this view
        // model, and re-picking the combination reading already holds must not come back
        // reported as somebody else's.
        var isSelfConflict = capturingTarget == HotkeyTarget.Reading
            ? candidate == savedReadingHotkey
            : candidate == pipeline.RegisteredHotkey || (stillLoading && candidate == startupHotkey);

        if (!isSelfConflict && !hotkeyAvailability.IsAvailable(candidate))
        {
            HotkeyHint = $"{HotkeyLabels.For(candidate)} ya lo está usando otra aplicación. Probá otra combinación.";
            return;
        }

        if (capturingTarget == HotkeyTarget.Reading)
            CapturedReading = candidate;
        else
            Captured = candidate;

        IsCapturingHotkey = false;
        HotkeyHint = "";
    }

    // ---- Startup registration failure ----

    /// <summary>
    /// Set from <see cref="Otto.App.App.ProvisionThenStartAsync"/>'s catch when
    /// <c>DictationPipeline.StartAsync</c> threw a <see cref="HotkeyRegistrationException"/>.
    /// Once that happens <see cref="State"/> is stuck at <see cref="DictationState.Loading"/>
    /// forever — nothing retries and nothing exits, because the hotkey <em>is</em> the
    /// dictation here — so <see cref="StatusText"/> and <see cref="EmptyHeading"/> both
    /// have to stop repeating "Cargando modelo…" and say what actually happened instead.
    /// </summary>
    [ObservableProperty] private bool hasHotkeyAlert;

    [ObservableProperty] private string hotkeyAlertMessage = "";

    /// <summary>
    /// Called only from <see cref="Otto.App.App"/>'s catch, already marshalled onto the
    /// UI thread the same way <see cref="OnPipelineStateChanged"/> is — this can only
    /// ever be reached from a startup failure caught off the UI thread, and
    /// <c>OnPropertyChanged</c> is not thread-safe.
    /// </summary>
    public void ShowHotkeyFailure(bool alreadyInUse)
    {
        HasHotkeyAlert = true;

        // ListeningLabel already falls back to startupHotkey while RegisteredHotkey is
        // null, and RegisteredHotkey stays null forever after this failure — so it
        // names the exact combination that just failed to register, with no separate
        // field to keep in sync.
        HotkeyAlertMessage = alreadyInUse
            ? $"No se pudo activar el atajo {ListeningLabel}: otra aplicación ya lo está usando. Elegí uno distinto en Configuración."
            : $"No se pudo activar el atajo {ListeningLabel}. Elegí uno distinto en Configuración.";

        OnPropertyChanged(nameof(StatusText));
        RefreshEmpty();
        OnPropertyChanged(nameof(IsListening));
        OnPropertyChanged(nameof(IsWorking));
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
        CharacterAppearance = Appearance,
        CheckForUpdates = CheckForUpdates,
        CorrectVoseo = CorrectVoseo,
        CorrectionIdleUnloadMinutes = CorrectionIdleUnloadMinutes,
        ReadAloud = ReadAloud,
        ReadingModifiers = CapturedReading.Modifiers,
        ReadingVirtualKey = CapturedReading.VirtualKey,
        ReadingHotkeyLabel = ReadingHotkeyLabel,
        ReadingVoice = ReadingVoice.Id,
        ReadingVoicing = ReadingVoicing.Id,
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
        CharacterAppearanceChanged?.Invoke(Appearance);

        // Gated on CorrectVoseoChangedSinceApplied, unlike the two events
        // above: CharacterVisibilityChanged's own handler is cheap and
        // idempotent, but this one can attempt a ~2 GB download — see that
        // property's own doc comment.
        if (CorrectVoseoChangedSinceApplied)
        {
            CorrectVoseoChanged?.Invoke(CorrectVoseo);
            lastAppliedCorrectVoseo = CorrectVoseo;
        }

        CorrectionIdleUnloadMinutesChanged?.Invoke(CorrectionIdleUnloadMinutes);

        // Gated for the same reason CorrectVoseoChanged is: App takes or releases a global
        // hotkey in response, and redoing that on every unrelated settings change is work
        // that can only fail, never succeed differently.
        if (ReadAloudChangedSinceApplied)
        {
            ReadAloudChanged?.Invoke(ReadAloud);
            lastAppliedReadAloud = ReadAloud;
        }

        // Gated, and unlike the dictation hotkey this one really does take effect now.
        // Dictation cannot rebind live — hence HotkeyChangePending's "applies on the next
        // launch" notice — because DictationPipeline owns its registration for the life of
        // the process. Reading's is already taken and released at runtime by
        // App.SetReadingEnabled every time the checkbox moves, so re-registering on a
        // changed binding is the same operation that path already performs.
        if (CapturedReading != savedReadingHotkey)
        {
            savedReadingHotkey = CapturedReading;
            ReadingHotkeyChanged?.Invoke(CapturedReading);
        }

        // Ungated, unlike the two above: telling the synthesiser which voice to use is a
        // field assignment, so the cheapest correct thing is to say it every time rather
        // than track whether it changed.
        ReadingVoiceChanged?.Invoke(ReadingVoice, ReadingVoicing);

        IsSettingsOpen = false;
    }

    /// <summary>
    /// Asks <c>App</c> to put the character back in its corner.
    ///
    /// <para>
    /// A command rather than a field on the settings form, because it is an action and not
    /// a preference: there is nothing to choose and nothing to save afterwards. It also
    /// deliberately does not wait for Guardar — somebody who has lost the character behind
    /// a window wants it back now, and making them find the save button first is asking
    /// them to trust that the thing they cannot see moved.
    /// </para>
    /// </summary>
    [RelayCommand]
    private void ResetCharacterPosition() => CharacterPositionResetRequested?.Invoke();

    /// <inheritdoc cref="ResetCharacterPositionCommand"/>
    public event Action? CharacterPositionResetRequested;

    /// <summary>
    /// Reflects a change made somewhere else — the tray menu — without saving or
    /// raising anything back, which would bounce between the two owners forever.
    /// </summary>
    public void ReflectCharacterVisibility(bool visible) => ShowCharacter = visible;

    /// <inheritdoc cref="ReflectCharacterVisibility"/>
    ///
    /// <remarks>
    /// Also records <paramref name="enabled"/> as the last-applied value —
    /// the tray already loaded or unloaded the corrector itself before
    /// calling this, so as far as <see cref="CorrectVoseoChangedSinceApplied"/>
    /// is concerned this IS an apply, not just a display update. Without
    /// this, the next unrelated <see cref="SaveSettings"/> would see
    /// <see cref="CorrectVoseo"/> differing from a stale
    /// <c>lastAppliedCorrectVoseo</c> and re-fire <see cref="CorrectVoseoChanged"/>
    /// for a change the tray already made.
    /// </remarks>
    public void ReflectCorrectVoseo(bool enabled)
    {
        CorrectVoseo = enabled;
        lastAppliedCorrectVoseo = enabled;
    }

    // ---- Updates ----

    [ObservableProperty] private bool checkForUpdates;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateStatus))]
    private string updateStatus = "";

    /// <summary>
    /// Keeps the status line out of the layout until there is something to say.
    /// Expressed here rather than as a converter in the view so the rule is one
    /// thing in one place — the same argument <see cref="IsShowingNotes"/> makes.
    /// </summary>
    public bool HasUpdateStatus => !string.IsNullOrEmpty(UpdateStatus);

    public string Version => UpdateChecker.Current;

    /// <summary>
    /// The last check's answer, kept so the install command has something to act
    /// on. Nothing else reads it: the button's visibility is
    /// <see cref="CanInstallUpdate"/>, decided once when the check lands, rather
    /// than re-derived from here on every binding evaluation.
    /// </summary>
    private UpdateStatus? lastCheck;

    /// <summary>Whether the "instalar" button is on screen at all.</summary>
    [ObservableProperty] private bool canInstallUpdate;

    /// <summary>Hides both buttons while a download is in flight, so neither can be started twice.</summary>
    [ObservableProperty] private bool isInstallingUpdate;

    /// <summary>
    /// Names the version, rather than saying "instalar". The same reason the
    /// uninstall confirmation counts the notes: "instalar 2.1.0" is something a
    /// person can agree to, "instalar" is something they have to trust.
    /// </summary>
    [ObservableProperty] private string installUpdateLabel = "Instalar";

    /// <summary>
    /// The installer is running and Otto has to get out of its way — Inno is about
    /// to overwrite the executable this process is running from. <c>App</c> shuts
    /// down on this exactly as it does for <see cref="UninstallRequested"/>.
    /// </summary>
    public event Action? UpdateInstallStarted;

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        UpdateStatus = "Buscando…";
        CanInstallUpdate = false;

        using var checker = new UpdateChecker(NullLogger<UpdateChecker>.Instance);
        var status = await checker.CheckAsync();

        lastCheck = status;

        UpdateStatus = status.Result switch
        {
            UpdateResult.Available => $"Hay una versión nueva: {status.LatestVersion}",
            UpdateResult.UpToDate  => $"Estás al día ({status.CurrentVersion})",
            _ => "No se pudo verificar. ¿Estás sin conexión?",
        };

        // Three conditions, and all three are real. There has to be something newer
        // that published a hash (CanInstall); there has to be a factory, which the
        // tests deliberately leave out; and this has to be the installed copy
        // rather than the portable one. Any of them false means the status line
        // still reports the new version and the user still gets to it through the
        // release page — the check is not degraded, only the shortcut.
        CanInstallUpdate = status.CanInstall && updateInstaller is not null && UpdateInstaller.CanSelfInstall;

        if (CanInstallUpdate) InstallUpdateLabel = $"Instalar {status.LatestVersion}";
    }

    /// <summary>
    /// The second deliberate click. See <see cref="UpdateInstaller"/> for why this
    /// is never reached without one.
    /// </summary>
    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (updateInstaller is null || lastCheck?.Installer is null) return;

        IsInstallingUpdate = true;
        CanInstallUpdate = false;

        try
        {
            using var installer = updateInstaller();

            // Constructed on the UI thread, so its callbacks come back to the UI
            // thread — the download reports from wherever the continuation lands.
            var progress = new Progress<int>(percent => UpdateStatus = $"Descargando… {percent}%");

            var result = await installer.InstallAsync(lastCheck.Installer, progress);

            UpdateStatus = result switch
            {
                UpdateInstallResult.Started =>
                    "Instalando. Otto se va a cerrar y volver solo.",
                UpdateInstallResult.ChecksumMismatch =>
                    "Lo descargado no coincide con lo publicado. No se instaló nada.",
                UpdateInstallResult.Unverifiable =>
                    "Esa versión no publicó su hash. Bajala a mano desde la página de la release.",
                UpdateInstallResult.NotInstalled =>
                    "Esta es la copia portable: bajá la versión nueva a mano.",
                UpdateInstallResult.CouldNotLaunch =>
                    "Se descargó, pero Windows no la pudo abrir. Está en el registro.",
                _ =>
                    "No se pudo descargar. ¿Estás sin conexión?",
            };

            if (result == UpdateInstallResult.Started)
            {
                UpdateInstallStarted?.Invoke();
                return;
            }

            // Only the two failures a second attempt could actually fix bring the
            // button back. A hash that did not match will not match on a retry, an
            // unpublished hash will not appear, and the portable copy will not
            // become an installed one — offering "reintentar" for any of those is
            // the same mistake the tray's correction indicator avoids by refusing
            // to show a retry that cannot succeed on that machine.
            CanInstallUpdate = result is UpdateInstallResult.DownloadFailed or UpdateInstallResult.CouldNotLaunch;
        }
        finally
        {
            IsInstallingUpdate = false;
        }
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

    /// <inheritdoc cref="IsShowingNotes"/>
    partial void OnIsProvisioningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsShowingNotes));
        OnPropertyChanged(nameof(IsShowingSettings));
    }

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

            case ProvisioningState.DownloadingCorrection:
                // Unlike the speech leg's copy, this must never say "solo la
                // primera vez": an upgrading Ollama-era user already has Whisper
                // installed and is not on a first run — the missing GGUF is the
                // whole reason this leg fires for them.
                IsProvisioning = true;
                HasProvisioningError = false;
                ProvisioningText = $"Descargando {provisioningOptions.CorrectionLabel} ({provisioningOptions.CorrectionSize})…";

                ProvisioningDetail = status.Progress is { } cp
                    ? $"{cp.Fraction:P0} · {cp.BytesPerSecond / 1024 / 1024:N1} MB/s"
                    : "Si se corta, la próxima vez continúa desde donde quedó.";
                ProvisioningPercent = status.Progress?.Fraction ?? 0;
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

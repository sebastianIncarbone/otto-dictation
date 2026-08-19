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
        ProvisioningOptions provisioningOptions,
        IHotkeyAvailability hotkeyAvailability,

        // Resolved lazily and optional for the same reason the clipboard is: it
        // belongs to a window, and this view model is built before there is one.
        // Left out — as every test leaves it out — exporting finds nowhere to put a
        // file and declines, which is the honest answer when there is no window to
        // ask through. Program.cs is the one place that passes it.
        Func<IStorageProvider?>? storage = null)
    {
        this.repository = repository;
        this.pipeline = pipeline;
        this.settingsStore = settingsStore;
        this.clipboard = clipboard;
        this.databasePath = databasePath;
        this.provisioningOptions = provisioningOptions;
        this.hotkeyAvailability = hotkeyAvailability;
        this.storage = storage;

        startupHotkey = settings.ToBinding();
        savedHotkey = startupHotkey;
        captured = startupHotkey;

        language = settings.Language;
        startWithWindows = settings.StartWithWindows;
        showCharacter = settings.ShowCharacter;
        appearance = settings.CharacterAppearance;
        checkForUpdates = settings.CheckForUpdates;

        pipeline.StateChanged += OnPipelineStateChanged;
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

        // A capture that committed a candidate (OfferKey's last branch) but was then
        // abandoned — the settings card closed, or the standalone Cancelar command
        // called directly — left Captured holding that candidate. ApplyTo writes
        // Captured unconditionally, so the next unrelated Guardar (a language change,
        // an autostart toggle) would have persisted a hotkey the user never asked to
        // save. Restoring to savedHotkey — what is actually on disk — is always safe
        // here even when nothing was ever committed, since Captured then already
        // equals savedHotkey and this is a no-op.
        Captured = savedHotkey;
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
        var isSelfConflict = candidate == pipeline.RegisteredHotkey || (stillLoading && candidate == startupHotkey);

        if (!isSelfConflict && !hotkeyAvailability.IsAvailable(candidate))
        {
            HotkeyHint = $"{HotkeyLabels.For(candidate)} ya lo está usando otra aplicación. Probá otra combinación.";
            return;
        }

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

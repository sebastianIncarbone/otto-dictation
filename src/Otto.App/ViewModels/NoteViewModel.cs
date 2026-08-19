using System.Threading.Tasks;
using Avalonia.Input.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Otto.Core;

namespace Otto.App.ViewModels;

/// <summary>
/// One saved dictation, editable in place.
///
/// Editing exists because Whisper will occasionally get a word wrong, and making
/// the user re-dictate a whole paragraph over one word is exactly the friction the
/// tool is supposed to remove.
/// </summary>
public sealed partial class NoteViewModel : ObservableObject
{
    private readonly INoteRepository repository;
    private readonly Func<IClipboard?> clipboard;

    /// <summary>
    /// What is on disk, so <see cref="CancelEdit"/> has something to go back to.
    /// Updated on save and nowhere else — these are not "the previous values", they
    /// are the saved ones.
    /// </summary>
    private string savedTitle;
    private string savedText;

    public NoteViewModel(Note note, INoteRepository repository, Func<IClipboard?> clipboard)
    {
        this.repository = repository;
        this.clipboard = clipboard;

        Id = note.Id;
        title = savedTitle = note.Title;
        text = savedText = note.Text;
        Created = note.CreatedAt.ToLocalTime();
        Source = note.Context.ProcessName;
        Seconds = note.AudioDuration.TotalSeconds;
    }

    public long Id { get; }
    public DateTimeOffset Created { get; }
    public string Source { get; }
    public double Seconds { get; }

    public string Heading => string.IsNullOrWhiteSpace(Title) ? "Sin título" : Title;

    /// <summary>
    /// Whether <see cref="Heading"/> is the note's own title or the stand-in. The
    /// design greys the stand-in out, and the difference has to be a property
    /// rather than a comparison in the view: "Sin título" is a legitimate thing
    /// for someone to actually name a note.
    /// </summary>
    public bool HasTitle => !string.IsNullOrWhiteSpace(Title);

    /// <summary>
    /// When, from where, and how long — the three things that tell one dictation
    /// apart from another when neither has been given a title.
    /// </summary>
    public string Subtitle
    {
        get
        {
            var line = $"{Created:dd/MM HH:mm}";

            if (!string.IsNullOrEmpty(Source)) line += $" · {Source}";

            // Below a second there is nothing worth reporting, and "0 s" reads as
            // a failure rather than as a very short note.
            if (Seconds >= 1) line += $" · {Seconds:0} s";

            return line;
        }
    }

    [ObservableProperty] private string title;
    [ObservableProperty] private string text;
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private bool isEditing;
    [ObservableProperty] private string copyLabel = "Copiar";

    partial void OnTitleChanged(string value)
    {
        IsDirty = true;
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(HasTitle));
    }

    partial void OnTextChanged(string value) => IsDirty = true;

    /// <summary>Raised when this note becomes the one being edited.</summary>
    public event Action<NoteViewModel>? EditStarted;

    /// <summary>
    /// Reading and editing are two different things, and the design draws them
    /// that way: a note is prose until someone decides to change it, and only then
    /// does it become a pair of fields.
    /// </summary>
    [RelayCommand]
    private void BeginEdit()
    {
        IsEditing = true;
        EditStarted?.Invoke(this);
    }

    /// <summary>
    /// Backs out, discarding whatever was typed. Deliberate — this is what Escape
    /// is for — which is why it may throw away work and
    /// <see cref="CloseEditor"/> may not.
    /// </summary>
    [RelayCommand]
    private void CancelEdit()
    {
        Title = savedTitle;
        Text = savedText;

        // After the assignments: setting either one marks the note dirty again.
        IsDirty = false;
        IsEditing = false;
    }

    /// <summary>
    /// Closes the editor because attention moved to another note — and refuses to
    /// when there is something unsaved, because nobody asked for it to be thrown
    /// away. Two notes open at once is untidy; a paragraph silently lost because a
    /// different row was clicked is not something the user can undo.
    /// </summary>
    public void CloseEditor()
    {
        if (IsDirty) return;

        IsEditing = false;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        await repository.UpdateAsync(Id, Title, Text);

        savedTitle = Title;
        savedText = Text;

        IsDirty = false;
        IsEditing = false;
    }

    /// <summary>
    /// One click copies the whole note. Selecting text by hand to copy it is the
    /// same kind of small friction Otto exists to remove, so the notes screen
    /// should not reintroduce it.
    /// </summary>
    [RelayCommand]
    private async Task CopyAsync()
    {
        if (clipboard() is not { } board) return;

        await board.SetTextAsync(Text);

        // Confirming in place beats a toast: the button the user just clicked is
        // already where they are looking.
        CopyLabel = "¡Copiado!";
        await Task.Delay(1200);
        CopyLabel = "Copiar";
    }

    public event Action<NoteViewModel>? DeleteRequested;

    [RelayCommand]
    private void Delete() => DeleteRequested?.Invoke(this);
}

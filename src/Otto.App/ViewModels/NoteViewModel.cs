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

    public NoteViewModel(Note note, INoteRepository repository, Func<IClipboard?> clipboard)
    {
        this.repository = repository;
        this.clipboard = clipboard;

        Id = note.Id;
        title = note.Title;
        text = note.Text;
        Created = note.CreatedAt.ToLocalTime();
        Source = note.Context.ProcessName;
        Seconds = note.AudioDuration.TotalSeconds;
    }

    public long Id { get; }
    public DateTimeOffset Created { get; }
    public string Source { get; }
    public double Seconds { get; }

    public string Heading => string.IsNullOrWhiteSpace(Title) ? "Sin título" : Title;

    public string Subtitle => string.IsNullOrEmpty(Source)
        ? $"{Created:dd/MM HH:mm}"
        : $"{Created:dd/MM HH:mm} · {Source}";

    [ObservableProperty] private string title;
    [ObservableProperty] private string text;
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private string copyLabel = "Copiar";

    partial void OnTitleChanged(string value)
    {
        IsDirty = true;
        OnPropertyChanged(nameof(Heading));
    }

    partial void OnTextChanged(string value) => IsDirty = true;

    [RelayCommand]
    private async Task SaveAsync()
    {
        await repository.UpdateAsync(Id, Title, Text);
        IsDirty = false;
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

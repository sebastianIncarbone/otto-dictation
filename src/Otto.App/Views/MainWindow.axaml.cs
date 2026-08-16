using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Otto.App.ViewModels;

namespace Otto.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // Loading on show rather than on construction keeps startup free of a
        // database read the user has not asked for yet.
        if (DataContext is MainViewModel view) _ = view.ReloadAsync();
    }
}

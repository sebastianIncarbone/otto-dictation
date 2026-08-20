using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Win32.Input;
using Otto.App.ViewModels;
using Otto.Core;

namespace Otto.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Tunnel + handledEventsToo: bubbling never sees Tab, Space, Enter or Escape —
        // a focused control consumes them first — and those are exactly the keys worth
        // binding or using to cancel a capture.
        AddHandler(KeyDownEvent, OnCaptureKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>
    /// Minimises. The bar is marked up with <c>WindowDecorationProperties</c>, so
    /// dragging, double-click to maximise and edge snapping are the system's job;
    /// what the buttons do when clicked is still ours.
    /// </summary>
    private void OnMinimiseClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    /// <summary>
    /// Closes, which the window's own Closing handler turns into hiding — Otto lives
    /// in the tray and the button on the bar means the same thing the system's one
    /// meant.
    /// </summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // Loading on show rather than on construction keeps startup free of a
        // database read the user has not asked for yet.
        if (DataContext is MainViewModel view) _ = view.ReloadAsync();
    }

    /// <summary>
    /// The only Avalonia-specific step in the capture feature: translate a raw key
    /// event into the <see cref="Otto.Core"/> types <see cref="MainViewModel.OfferKey"/>
    /// takes. The state machine itself lives entirely in the view model.
    /// </summary>
    private void OnCaptureKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel view || !view.IsCapturingHotkey) return;

        e.Handled = true; // nothing pressed while capturing should also trigger a button or move focus

        // KeyInterop.VirtualKeyFromKey returns int (a raw Win32 VK code); HotkeyBinding
        // uses uint to match RegisterHotKey's own signature.
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(e.Key);

        view.OfferKey(ToHotkeyModifiers(e.KeyModifiers), virtualKey);
    }

    private static HotkeyModifiers ToHotkeyModifiers(KeyModifiers modifiers)
    {
        var result = HotkeyModifiers.None;

        if (modifiers.HasFlag(KeyModifiers.Control)) result |= HotkeyModifiers.Control;
        if (modifiers.HasFlag(KeyModifiers.Alt)) result |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(KeyModifiers.Shift)) result |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(KeyModifiers.Meta)) result |= HotkeyModifiers.Windows;

        return result;
    }
}

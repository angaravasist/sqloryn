using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Sqloryn;

/// <summary>Launch panel. Raises intent events; the window owns what they do.</summary>
public partial class WelcomeView : UserControl
{
    public event EventHandler<RoutedEventArgs>? ConnectRequested;
    public event EventHandler<RoutedEventArgs>? NewQueryRequested;
    public event EventHandler<RoutedEventArgs>? OpenRequested;

    public WelcomeView() => InitializeComponent();

    void OnConnectClick(object? sender, RoutedEventArgs e) => ConnectRequested?.Invoke(this, e);
    void OnNewQueryClick(object? sender, RoutedEventArgs e) => NewQueryRequested?.Invoke(this, e);
    void OnOpenClick(object? sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, e);
}

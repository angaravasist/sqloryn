using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Sqloryn;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            desktop.ShutdownRequested += async (_, e) =>
            {
                if (desktop.MainWindow is MainWindow main && !await main.ConfirmQuitAsync())
                    e.Cancel = true;
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}

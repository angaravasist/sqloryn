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
            desktop.ShutdownRequested += (_, e) =>
            {
                // Cancel synchronously before any async prompt; complete shutdown only after agreement.
                if (desktop.MainWindow is not MainWindow main)
                    return;
                if (main.TryBeginQuit(out var completion))
                    return;
                e.Cancel = true;
                _ = CompleteShutdownAsync(desktop, completion);
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    static async Task CompleteShutdownAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        Task<bool> completion)
    {
        if (await completion.ConfigureAwait(true))
            desktop.Shutdown();
    }
}

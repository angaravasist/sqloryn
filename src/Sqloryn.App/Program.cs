using Avalonia;
using System;

namespace Sqloryn;

static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Resolve content relative to the executable so paths with spaces and
        // double-click launches do not depend on the IDE working directory.
        var baseDir = AppContext.BaseDirectory;
        Directory.SetCurrentDirectory(baseDir);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
}

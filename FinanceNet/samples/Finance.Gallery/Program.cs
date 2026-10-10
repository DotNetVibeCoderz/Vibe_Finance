using Avalonia;
using Finance.Gallery.Shell;

namespace Finance.Gallery;

/// <summary>
/// Finance.Net Gallery. Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
/// <c>--screenshot &lt;dir&gt;</c> renders every module to PNG headlessly and exits (used for README and docs).
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        int i = Array.IndexOf(args, "--screenshot");
        if (i >= 0) return ScreenshotRunner.Run(i + 1 < args.Length ? args[i + 1] : "docs/screenshots");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}

using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Finance.Gallery.Shell;

/// <summary>
/// Renders every page to PNG through Avalonia.Headless with real Skia drawing, so captures are
/// 1440×900 regardless of the physical screen.
/// <c>dotnet run --project samples/Finance.Gallery -- --screenshot docs/screenshots</c>
/// </summary>
public static class ScreenshotRunner
{
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory);
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();

        var window = new MainWindow { Width = 1440, Height = 900 };
        window.Show();
        Pump(TimeSpan.FromMilliseconds(300));

        for (int i = 0; i < window.Pages.Count; i++)
        {
            var page = window.Pages[i];
            window.Navigate(page);
            Pump(TimeSpan.FromMilliseconds(400));
            var ready = page.WhenReadyAsync();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
            while (!ready.IsCompleted && DateTime.UtcNow < deadline) Pump(TimeSpan.FromMilliseconds(100));
            if (!ready.IsCompleted) Console.Error.WriteLine($"{page.Title}: timed out waiting for content");
            else if (ready.IsFaulted) Console.Error.WriteLine($"{page.Title}: {ready.Exception?.GetBaseException().Message}");
            Pump(TimeSpan.FromMilliseconds(600));

            var frame = window.CaptureRenderedFrame();
            string file = Path.Combine(directory, $"{i:00}-{page.Slug}.png");
            if (frame is not null)
            {
                using var fs = File.Create(file);
                frame.Save(fs, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            Console.WriteLine($"saved {file}");
        }
        window.Close();
        return 0;
    }

    /// <summary>Runs dispatcher jobs and render ticks for roughly <paramref name="duration"/>.</summary>
    private static void Pump(TimeSpan duration)
    {
        var end = DateTime.UtcNow + duration;
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(15);
        } while (DateTime.UtcNow < end);
        Dispatcher.UIThread.RunJobs();
    }
}

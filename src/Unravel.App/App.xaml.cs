using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Unravel.App.ViewModels;
using Unravel.Core.Collectors;
using Unravel.Core.Infrastructure;

namespace Unravel.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        // Developer aid: `--fixture file.json` shows a machine captured with --capture-fixture instead of this PC.
        var fixturePath = Program.Arg(e.Args, "--fixture");
        Func<CollectContext> context = fixturePath is null
            ? CollectContext.Live
            : () => new CollectContext(new FixtureSystemSource(SystemFixture.Load(fixturePath)), IsAdmin: false, DateTimeOffset.Now);

        var window = new MainWindow();
        var viewModel = new MainViewModel(ScanRunner.CreateDefault, context, window);
        window.DataContext = viewModel;
        MainWindow = window;

        // Developer aid: `Unravel.exe --screenshot file.png` renders the Summary page to an image after the scan and exits.
        var screenshotPath = Program.Arg(e.Args, "--screenshot");
        if (screenshotPath is not null)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.ShowActivated = false;
            // Developer aid: `--page Hardware` shows that page before the screenshot.
            if (Program.Arg(e.Args, "--page") is { } page && Enum.TryParse<AppPage>(page, true, out var p))
            {
                viewModel.CurrentPage = p;
            }

            viewModel.ScanCompleted += async (_, _) =>
            {
                await Task.Delay(800);
                SaveScreenshot(window.RootContent, screenshotPath);
                // Developer aid: `--screenshot-window` also saves the whole window (sidebar included) next to it.
                if (e.Args.Contains("--screenshot-window", StringComparer.OrdinalIgnoreCase))
                {
                    SaveScreenshot(window.WindowContent, Path.ChangeExtension(screenshotPath, null) + "-window.png");
                }
                Shutdown();
            };
        }

        window.Show();
        _ = viewModel.ScanAsync();
    }

    private static void SaveScreenshot(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var width = (int)Math.Ceiling(element.ActualWidth);
        var height = (int)Math.Ceiling(element.ActualHeight);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private bool _showingError;

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Console.Error.WriteLine(e.Exception);

        // The message box pumps messages: if the same error happens again meanwhile (a layout error repeats on every
        // pass), showing another box would nest them until the stack overflows. One at a time.
        if (_showingError)
        {
            return;
        }

        _showingError = true;
        try
        {
            // Degrade gracefully: tell the user, keep the app running.
            MessageBox.Show(MainWindow, $"Something went wrong: {e.Exception.Message}", "Unravel",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _showingError = false;
        }
    }
}

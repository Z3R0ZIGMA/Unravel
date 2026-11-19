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
            viewModel.ScanCompleted += async (_, _) =>
            {
                await Task.Delay(800);
                SaveScreenshot(window.RootContent, screenshotPath);
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

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Degrade gracefully: tell the user, keep the app running.
        MessageBox.Show(MainWindow, $"Something went wrong: {e.Exception.Message}", "Unravel",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}

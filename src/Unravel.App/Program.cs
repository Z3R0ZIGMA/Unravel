using Unravel.Core.Collectors;
using Unravel.Core.Infrastructure;

namespace Unravel.App;

/// <summary>
/// Entry point. <c>Unravel.exe --capture-fixture file.json</c> runs a scan with no window and saves everything it read
/// (for test fixtures). Anything else starts the normal app.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var capture = Arg(args, "--capture-fixture");
        if (capture is not null)
        {
            return CaptureFixture(capture);
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>The value after <paramref name="name"/>, or null.</summary>
    internal static string? Arg(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int CaptureFixture(string path)
    {
        var recorder = new RecordingSystemSource(new LiveSystemSource());
        var ctx = new CollectContext(recorder, Elevation.IsAdmin(), DateTimeOffset.Now);
        ScanRunner.CreateDefault().RunAsync(ctx).GetAwaiter().GetResult();
        recorder.ToFixture($"Captured on {Environment.MachineName}").Save(path);
        return 0;
    }
}

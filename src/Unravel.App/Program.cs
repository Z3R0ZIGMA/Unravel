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

        var dump = Arg(args, "--dump");
        if (dump is not null)
        {
            return Dump(dump, Arg(args, "--fixture"));
        }

        // Developer aid: `--active-test disk|network <file.json>` runs one active test with no window.
        var activeTest = Arg(args, "--active-test");
        if (activeTest is not null)
        {
            var output = args.SkipWhile(a => !string.Equals(a, "--active-test", StringComparison.OrdinalIgnoreCase)).Skip(2).FirstOrDefault() ?? "active-test.json";
            Core.Diagnostics.TestOutcome outcome = activeTest switch
            {
                "disk" => Core.Diagnostics.DiskSpeedTest.RunAsync(256, System.IO.Path.GetTempPath(), null, null, default).GetAwaiter().GetResult(),
                "cpu" => Core.Diagnostics.CpuStressTest.RunAsync(30, null, default).GetAwaiter().GetResult(),
                _ => Core.Diagnostics.NetworkTest.RunAsync(null, args.Contains("--download"), null, default).GetAwaiter().GetResult(),
            };
            System.IO.File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(outcome,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
            return 0;
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

    /// <summary>Developer aid: the parsed scan (every section's data and warnings) as JSON.</summary>
    private static int Dump(string path, string? fixture)
    {
        var ctx = fixture is null
            ? CollectContext.Live()
            : new CollectContext(new FixtureSystemSource(SystemFixture.Load(fixture)), false, DateTimeOffset.Now);
        var scan = ScanRunner.CreateDefault().RunAsync(ctx).GetAwaiter().GetResult();
        var sections = scan.Results.Select(r => new
        {
            r.Section,
            Seconds = Math.Round(r.Duration.TotalSeconds, 2),
            r.Warnings,
            r.NeedsAdmin,
            r.Data,
        });
        var options = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
        System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(sections, options));
        return 0;
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

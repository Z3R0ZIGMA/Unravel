using Unravel.Core.Collectors;
using Unravel.Core.Infrastructure;
using Unravel.Core.Models;

namespace Unravel.Core.Tests;

/// <summary>A failing or hung collector must never crash or freeze the scan (plan.md sections 3 and 10).</summary>
public class ScanRunnerTests
{
    private static CollectContext Context(ISystemSource? source = null) =>
        new(source ?? new FixtureSystemSource(new SystemFixture()), IsAdmin: false, DateTimeOffset.Now);

    private sealed class ThrowingCollector : ICollector
    {
        public string Section => "Boom";

        public Task<CollectorResult> CollectAsync(CollectContext ctx, CancellationToken ct) => throw new InvalidOperationException("kaput");
    }

    private sealed class HangingCollector : ICollector
    {
        public string Section => "Slow";

        public async Task<CollectorResult> CollectAsync(CollectContext ctx, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, CancellationToken.None);
            return new CollectorResult { Section = Section };
        }
    }

    private sealed class ThrowingSource : ISystemSource
    {
        public IReadOnlyList<WmiRecord> Query(string wmiNamespace, string className, IReadOnlyList<string> properties, string? where = null) =>
            throw new NullReferenceException("a bug in a source");

        public object? RegistryValue(string key, string name) => null;

        public bool RegistryKeyExists(string key) => false;
    }

    [Fact]
    public async Task ThrowingCollector_BecomesAFailedResult()
    {
        var scan = await new ScanRunner([new ThrowingCollector(), new DeviceCollector()]).RunAsync(Context());

        var boom = scan.ResultFor("Boom")!;
        Assert.True(boom.Failed);
        Assert.Equal(["Couldn't read boom (kaput)"], boom.Warnings);
    }

    [Fact]
    public async Task HungCollector_TimesOut_OthersStillReport()
    {
        var reported = new List<string>();
        var progress = new SyncProgress(r => reported.Add(r.Section));
        var scan = await new ScanRunner([new HangingCollector(), new ThrowingCollector()], TimeSpan.FromMilliseconds(200))
            .RunAsync(Context(), progress);

        Assert.Contains("Couldn't read slow (no answer after 0 s)", scan.ResultFor("Slow")!.Warnings);
        Assert.Equal(2, reported.Count);
    }

    [Fact]
    public async Task UnexpectedExceptionInsideACollector_IsCaught()
    {
        var scan = await ScanRunner.CreateDefault().RunAsync(Context(new ThrowingSource()));

        Assert.Equal(5, scan.Results.Count);
        Assert.All(scan.Results, r => Assert.True(r.Failed));
        Assert.All(scan.Results, r => Assert.NotEmpty(r.Warnings));
    }

    [Fact]
    public async Task EmptyMachine_EverySectionFailsWithAReason()
    {
        // A fixture with nothing in it: every WMI class is "not available".
        var scan = await ScanRunner.CreateDefault().RunAsync(Context());

        Assert.All(scan.Results, r => Assert.True(r.Failed, r.Section));
        Assert.Contains(scan.Warnings, w => w.StartsWith("Couldn't read processor", StringComparison.Ordinal));
        Assert.Equal(Sections.Device, scan.Results[0].Section);
    }

    [Fact]
    public void Win32Product_IsNeverQueried() =>
        Assert.Throws<InvalidOperationException>(() => Wmi.Query(WmiNamespaces.Cimv2, "Win32_Product", ["Name"]));

    private sealed class SyncProgress(Action<CollectorResult> report) : IProgress<CollectorResult>
    {
        public void Report(CollectorResult value)
        {
            lock (this)
            {
                report(value);
            }
        }
    }
}

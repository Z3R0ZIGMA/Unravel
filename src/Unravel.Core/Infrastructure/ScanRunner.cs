using System.Diagnostics;
using Unravel.Core.Collectors;
using Unravel.Core.Models;

namespace Unravel.Core.Infrastructure;

/// <summary>
/// Runs all collectors in parallel, each with its own timeout (default 20 s), so one hung WMI query cannot freeze the
/// scan. Each result is reported through <see cref="IProgress{T}"/> as soon as it is ready.
/// </summary>
public sealed class ScanRunner(IReadOnlyList<ICollector> collectors, TimeSpan? perCollectorTimeout = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>All collectors, in the order the sections appear.</summary>
    public static ScanRunner CreateDefault() => new(
    [
        new DeviceCollector(),
        new CpuCollector(),
        new MemoryCollector(),
        new StorageCollector(),
        new OsCollector(),
        new GpuDisplayCollector(),
        new FirmwareCollector(),
        new NetworkCollector(),
        new BatteryCollector(),
        new SoftwareCollector(),
        new SecurityCollector(),
        new IdentityCollector(),
        new StartupServicesCollector(),
        new DriverCollector(),
        new UpdatesCollector(),
        new StabilityCollector(),
    ]);

    public IReadOnlyList<string> Sections => collectors.Select(c => c.Section).ToList();

    public async Task<ScanResult> RunAsync(CollectContext ctx, IProgress<CollectorResult>? progress = null, CancellationToken ct = default)
    {
        var timeout = perCollectorTimeout ?? DefaultTimeout;
        var watch = Stopwatch.StartNew();

        var tasks = collectors.Select(async collector =>
        {
            var result = await RunOne(collector, ctx, timeout, ct).ConfigureAwait(false);
            progress?.Report(result);
            return result;
        }).ToList();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return new ScanResult
        {
            StartedAt = ctx.Now,
            Duration = watch.Elapsed,
            IsAdmin = ctx.IsAdmin,
            Results = results,
        };
    }

    private static async Task<CollectorResult> RunOne(ICollector collector, CollectContext ctx, TimeSpan timeout, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            // Task.Run so a collector that blocks before its first await still cannot hold up the others.
            return await Task.Run(() => collector.CollectAsync(ctx, ct), ct).WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return CollectorResult.Failure(collector.Section, $"no answer after {timeout.TotalSeconds:0} s", watch.Elapsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A collector must never crash the app.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return CollectorResult.Failure(collector.Section, ex.Message, watch.Elapsed);
        }
    }
}

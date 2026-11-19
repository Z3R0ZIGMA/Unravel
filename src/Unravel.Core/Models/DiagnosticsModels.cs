namespace Unravel.Core.Models;

// ---------------------------------------------------------------- Windows Update (plan.md 4.4)

public sealed record UpdateEntry(string Title, DateTimeOffset Date, bool Succeeded, bool IsUninstall)
{
    /// <summary>Monthly cumulative / security updates, as opposed to Defender definitions, drivers and the like.</summary>
    public bool IsQualityUpdate =>
        Title.Contains("Cumulative Update", StringComparison.OrdinalIgnoreCase)
        || Title.Contains("Security Update", StringComparison.OrdinalIgnoreCase)
        || Title.Contains("Security Monthly Quality Rollup", StringComparison.OrdinalIgnoreCase);
}

public sealed record UpdatesInfo
{
    /// <summary>Newest first.</summary>
    public IReadOnlyList<UpdateEntry> History { get; init; } = [];

    /// <summary>The Windows Update service's start mode ("Manual", "Disabled"...).</summary>
    public string? ServiceStartMode { get; init; }

    public string? ServiceState { get; init; }

    public UpdateEntry? LastInstalled => History.FirstOrDefault(u => u is { Succeeded: true, IsUninstall: false });

    public UpdateEntry? LastQualityUpdate => History.FirstOrDefault(u => u is { Succeeded: true, IsUninstall: false, IsQualityUpdate: true });

    /// <summary>Failed installs that have not succeeded since (a retry that worked is not a problem).</summary>
    public IEnumerable<UpdateEntry> RecentFailures => History
        .Where(u => !u.Succeeded && !History.Any(s => s.Succeeded && s.Date >= u.Date && string.Equals(s.Title, u.Title, StringComparison.OrdinalIgnoreCase)))
        .DistinctBy(u => u.Title, StringComparer.OrdinalIgnoreCase)
        .Take(10);
}

// ---------------------------------------------------------------- event log and stability (plan.md 5.2)

/// <summary>One event of interest: a blue screen, an unexpected shutdown, a disk or hardware error.</summary>
public sealed record StabilityEvent(DateTimeOffset Time, string Detail, int? EventId = null)
{
    /// <summary>Disk event 153 ("I/O retried") is often a burst after resuming from sleep; 7 and 51 are real read failures.</summary>
    public bool IsRetryOnly => EventId == 153;
}

/// <summary>Events of the same kind within a few minutes of each other, counted once.</summary>
public sealed record EventBurst(DateTimeOffset Start, int Count, string Detail);

public sealed record AppCrash(string App, int Count, DateTimeOffset LastCrash, string? Module);

public sealed record ReliabilityPoint(DateOnly Day, double Index);

public sealed record MemoryTestResult(DateTimeOffset Time, bool ErrorsFound);

public sealed record StabilityInfo
{
    /// <summary>The window the counts cover (30 days).</summary>
    public int Days { get; init; } = 30;

    public IReadOnlyList<StabilityEvent> BlueScreens { get; init; } = [];

    public IReadOnlyList<StabilityEvent> UnexpectedShutdowns { get; init; } = [];

    public IReadOnlyList<StabilityEvent> DiskErrors { get; init; } = [];

    public IReadOnlyList<StabilityEvent> HardwareErrors { get; init; } = [];

    /// <summary>Application crashes grouped by program, most frequent first.</summary>
    public IReadOnlyList<AppCrash> AppCrashes { get; init; } = [];

    /// <summary>Crash dump files in C:\Windows\Minidump (null when the folder could not be read: it needs admin).</summary>
    public IReadOnlyList<string>? Minidumps { get; init; }

    /// <summary>Reliability Monitor's daily stability index (1-10), oldest first. Empty when Windows does not record it.</summary>
    public IReadOnlyList<ReliabilityPoint> Reliability { get; init; } = [];

    /// <summary>The last Windows Memory Diagnostic result, at any age.</summary>
    public MemoryTestResult? LastMemoryTest { get; init; }

    public int InLast(IEnumerable<StabilityEvent> events, int days, DateTimeOffset now) => events.Count(e => e.Time >= now.AddDays(-days));

    /// <summary>Disk errors grouped into bursts (events less than 10 minutes apart), newest first.</summary>
    public IReadOnlyList<EventBurst> DiskErrorBursts => Bursts(DiskErrors, TimeSpan.FromMinutes(10));

    public static IReadOnlyList<EventBurst> Bursts(IEnumerable<StabilityEvent> events, TimeSpan gap)
    {
        var bursts = new List<EventBurst>();
        DateTimeOffset? last = null;
        foreach (var e in events.OrderBy(e => e.Time))
        {
            if (last is { } l && e.Time - l <= gap && bursts.Count > 0)
            {
                var b = bursts[^1];
                bursts[^1] = b with { Count = b.Count + 1 };
            }
            else
            {
                bursts.Add(new EventBurst(e.Time, 1, e.Detail));
            }

            last = e.Time;
        }

        bursts.Reverse();
        return bursts;
    }
}

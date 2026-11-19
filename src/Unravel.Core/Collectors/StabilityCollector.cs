using System.Globalization;
using Unravel.Core.Infrastructure;
using Unravel.Core.Models;

namespace Unravel.Core.Collectors;

/// <summary>
/// Event log and stability for the last 30 days (plan.md section 5.2): blue screens, unexpected shutdowns, disk and
/// WHEA hardware errors, application crashes, minidumps, the Reliability Monitor index and the last memory test.
/// Read with EventLogReader and XPath; works as a standard user.
/// </summary>
public sealed class StabilityCollector : CollectorBase<StabilityInfo>
{
    public override string Section => Sections.Stability;

    public const int Days = 30;

    private const string Last30Days = "TimeCreated[timediff(@SystemTime) <= 2592000000]";

    /// <summary>One System-log query for everything: fewer passes over the log.</summary>
    public static readonly string SystemQuery = RowKinds.EventQuery("System",
        "*[System[(" +
        "(EventID=1001 and Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting']) or " +
        "(EventID=41 and Provider[@Name='Microsoft-Windows-Kernel-Power']) or " +
        "(EventID=6008 and Provider[@Name='EventLog']) or " +
        "((EventID=7 or EventID=51 or EventID=153) and (Provider[@Name='disk'] or Provider[@Name='storport'])) or " +
        "(Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (Level=1 or Level=2 or Level=3))" +
        $") and {Last30Days}]]");

    public static readonly string AppCrashQuery = RowKinds.EventQuery("Application",
        $"*[System[Provider[@Name='Application Error'] and EventID=1000 and {Last30Days}]]");

    public static readonly string MemoryTestQuery = RowKinds.EventQuery("System",
        "*[System[Provider[@Name='Microsoft-Windows-MemoryDiagnostics-Results'] and (EventID=1201 or EventID=1202)]]");

    protected override StabilityInfo Collect(CollectContext ctx, CollectorNotes notes, CancellationToken ct)
    {
        var since = ctx.Now.AddDays(-Days);
        var system = TryRows(ctx, notes, "the System event log", RowKinds.Events, SystemQuery)
            .Select(e => (Row: e, Time: Time(e)))
            .Where(e => e.Time >= since)
            .ToList();
        ct.ThrowIfCancellationRequested();

        List<StabilityEvent> Pick(Func<WmiRecord, bool> match, Func<WmiRecord, string> detail) =>
            system.Where(e => match(e.Row)).Select(e => new StabilityEvent(e.Time!.Value, detail(e.Row), e.Row.Int("Id"))).ToList();

        var blueScreens = Pick(e => e.Int("Id") == 1001, e => StabilityRules.BugCheck(e.RawString("Data0")));
        var kernelPower = Pick(e => e.Int("Id") == 41, e => StabilityRules.KernelPower41(e.RawString("Data0")));
        var eventLog6008 = Pick(e => e.Int("Id") == 6008, _ => "The previous shutdown was unexpected");
        var disk = Pick(e => e.RawString("Provider") is "disk" or "storport",
            e => StabilityRules.DiskError(e.Int("Id"), e.RawString("Data0")));
        var whea = Pick(e => e.RawString("Provider") == "Microsoft-Windows-WHEA-Logger",
            e => e.Int("Level") == 3 ? $"Corrected hardware error (event {e.Int("Id")})" : $"Hardware error (event {e.Int("Id")})");

        var crashes = TryRows(ctx, notes, "the Application event log", RowKinds.Events, AppCrashQuery)
            .Select(e => (App: e.RawString("Data0"), Module: e.RawString("Data3"), Time: Time(e)))
            .Where(e => e.App is not null && e.Time >= since)
            .GroupBy(e => e.App!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AppCrash(g.Key, g.Count(), g.Max(e => e.Time!.Value),
                g.GroupBy(e => e.Module).OrderByDescending(m => m.Count()).First().Key))
            .OrderByDescending(c => c.Count)
            .ThenByDescending(c => c.LastCrash)
            .ToList();

        var memory = TryRows(ctx, new CollectorNotes(), "memory test results", RowKinds.Events, MemoryTestQuery)
            .Select(e => (Id: e.Int("Id"), Time: Time(e)))
            .Where(e => e.Time is not null)
            .Select(e => new MemoryTestResult(e.Time!.Value, e.Id == 1202))
            .FirstOrDefault();

        // C:\Windows\Minidump is only readable by administrators.
        var minidumpFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");
        IReadOnlyList<string>? dumps = ctx.IsAdmin
            ? ctx.Source.Rows(RowKinds.FolderFiles, minidumpFolder).Select(f => f.RawString("Name")).OfType<string>().ToList()
            : null;
        if (!ctx.IsAdmin)
        {
            notes.NeedAdmin();
        }

        return new StabilityInfo
        {
            Days = Days,
            BlueScreens = blueScreens,
            // Kernel-Power 41 and EventLog 6008 usually describe the same event: count 41s, fall back to 6008s.
            UnexpectedShutdowns = kernelPower.Count > 0 ? kernelPower : eventLog6008,
            DiskErrors = disk,
            HardwareErrors = whea,
            AppCrashes = crashes,
            Minidumps = dumps,
            Reliability = ReadReliability(ctx, since),
            LastMemoryTest = memory,
        };
    }

    /// <summary>Reliability Monitor's index, one value per day (the last of the day). Quietly empty when not recorded.</summary>
    private static List<ReliabilityPoint> ReadReliability(CollectContext ctx, DateTimeOffset since) =>
        TryQuery(ctx, new CollectorNotes(), "reliability history", WmiNamespaces.Cimv2, "Win32_ReliabilityStabilityMetrics",
                ["SystemStabilityIndex", "TimeGenerated"])
            .Select(r => (Time: r.Date("TimeGenerated"), Index: r["SystemStabilityIndex"]))
            .Where(r => r.Time >= since && r.Index is not null)
            .GroupBy(r => DateOnly.FromDateTime(r.Time!.Value.LocalDateTime))
            .Select(g => new ReliabilityPoint(g.Key, Convert.ToDouble(g.OrderBy(r => r.Time).Last().Index, CultureInfo.InvariantCulture)))
            .OrderBy(p => p.Day)
            .ToList();

    private static DateTimeOffset? Time(WmiRecord e) =>
        DateTimeOffset.TryParse(e.RawString("Time"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
}

public static class StabilityRules
{
    /// <summary>"0x0000009f (0x..., ...)" to "Bug check 0x9F DRIVER_POWER_STATE_FAILURE".</summary>
    public static string BugCheck(string? data)
    {
        if (data is null)
        {
            return "Blue screen";
        }

        var code = data.Split(' ', '(')[0].Trim();
        if (!code.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !uint.TryParse(code[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return $"Blue screen ({data.Trim()})";
        }

        return BugCheckNames.TryGetValue(value, out var name) ? $"Blue screen 0x{value:X}: {name}" : $"Blue screen 0x{value:X}";
    }

    /// <summary>Kernel-Power 41 data 0 is the bug check code in decimal (0 = power lost or hard reset, no blue screen).</summary>
    public static string KernelPower41(string? bugcheck) =>
        long.TryParse(bugcheck, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) && code != 0
            ? $"Restarted after a blue screen (0x{code:X})"
            : "Shut down without warning (power loss, forced power-off or a hang)";

    public static string DiskError(int? id, string? device) => id switch
    {
        7 => $"Bad block on {device ?? "a disk"}",
        51 => $"Error during a paging operation on {device ?? "a disk"}",
        153 => $"Disk I/O retried on {device ?? "a disk"}",
        _ => $"Disk error (event {id})",
    };

    private static readonly Dictionary<uint, string> BugCheckNames = new()
    {
        [0x0A] = "IRQL_NOT_LESS_OR_EQUAL",
        [0x1A] = "MEMORY_MANAGEMENT",
        [0x1E] = "KMODE_EXCEPTION_NOT_HANDLED",
        [0x3B] = "SYSTEM_SERVICE_EXCEPTION",
        [0x50] = "PAGE_FAULT_IN_NONPAGED_AREA",
        [0x7A] = "KERNEL_DATA_INPAGE_ERROR",
        [0x7E] = "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED",
        [0x9F] = "DRIVER_POWER_STATE_FAILURE",
        [0xA0] = "INTERNAL_POWER_ERROR",
        [0xC2] = "BAD_POOL_CALLER",
        [0xD1] = "DRIVER_IRQL_NOT_LESS_OR_EQUAL",
        [0xEF] = "CRITICAL_PROCESS_DIED",
        [0x101] = "CLOCK_WATCHDOG_TIMEOUT",
        [0x116] = "VIDEO_TDR_FAILURE",
        [0x124] = "WHEA_UNCORRECTABLE_ERROR",
        [0x133] = "DPC_WATCHDOG_VIOLATION",
        [0x139] = "KERNEL_SECURITY_CHECK_FAILURE",
        [0x154] = "UNEXPECTED_STORE_EXCEPTION",
        [0x15F] = "CONNECTED_STANDBY_WATCHDOG_TIMEOUT_LIVEDUMP",
        [0x1A8] = "BUGCODE_NDIS_DRIVER_LIVE_DUMP",
    };
}

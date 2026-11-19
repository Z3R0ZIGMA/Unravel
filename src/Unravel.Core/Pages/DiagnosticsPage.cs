using System.Globalization;
using Unravel.Core.Diagnostics;
using Unravel.Core.Models;

namespace Unravel.Core.Pages;

/// <summary>
/// The report part of the Diagnostics page (plan.md section 5): every finding, Windows 11 readiness, support and
/// updates, and stability. The active tests are interactive and live in the app.
/// </summary>
public static class DiagnosticsPage
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    public static IReadOnlyList<CardModel> Build(ScanResult scan, ICollection<string> pending) =>
    [
        Findings(scan, pending),
        Windows11(scan, pending),
        SupportAndUpdates(scan, pending),
        Stability(scan, pending),
    ];

    private static CardModel Findings(ScanResult scan, ICollection<string> pending)
    {
        var findings = HealthRules.Evaluate(scan).OrderByDescending(f => f.Severity).ThenBy(f => f.Area, StringComparer.Ordinal).ToList();
        var counts = string.Join(", ", new[] { Severity.Critical, Severity.Warning, Severity.Info }
            .Select(s => (s, n: findings.Count(f => f.Severity == s)))
            .Where(x => x.n > 0)
            .Select(x => $"{x.n} {x.s switch { Severity.Critical => "critical", Severity.Warning => "warning", _ => "info" }}{(x.n == 1 || x.s == Severity.Info ? "" : "s")}"));
        return new CardModel
        {
            Title = "Health checks",
            Subtitle = pending.Count > 0 ? "Still reading; more checks will appear." : findings.Count == 0 ? "Everything checked looks good." : counts,
            Lines = findings.Count == 0
                ? [new StatusLine(Severity.Ok, "No problems found")]
                : findings.Select(f => new StatusLine(f.Severity, $"{f.Area}: {f.Message}", f.Recommendation)).ToList(),
        };
    }

    private static CardModel Windows11(ScanResult scan, ICollection<string> pending)
    {
        if (scan.Cpu is null && pending.Contains(Sections.Cpu) || scan.Firmware is null && pending.Contains(Sections.Firmware))
        {
            return new CardModel { Title = "Windows 11 readiness", State = "Reading..." };
        }

        var r = Win11Check.Evaluate(scan);
        return new CardModel
        {
            Title = "Windows 11 readiness",
            Subtitle = r.Summary,
            Lines = r.Checks.Select(c => new StatusLine(c.Result switch
            {
                CheckResult.Pass => Severity.Ok,
                CheckResult.Fail => Severity.Critical,
                _ => (Severity?)null,
            }, $"{c.Requirement}: {c.Detail}", c.Fix)).ToList(),
            Notes = ["The processor is checked by family and generation against Microsoft's rules (Intel 8th gen, AMD Ryzen 2000 Zen+, Qualcomm Snapdragon 850 and later). Models it cannot place show as unknown."],
        };
    }

    private static CardModel SupportAndUpdates(ScanResult scan, ICollection<string> pending)
    {
        var fields = new List<PageField>();
        if (scan.Os is { } os)
        {
            var support = Lifecycle.Current.Check(os, scan.StartedAt);
            fields.Add(new("Windows support", support.Text, Severity: support.State switch
            {
                SupportState.Supported or SupportState.NewerThanTable => Severity.Ok,
                SupportState.EndingSoon => Severity.Info,
                SupportState.OutOfSupport => Severity.Critical,
                _ => null,
            }));
            fields.Add(new("Pending restart", os.PendingReboot ? $"Yes ({string.Join(", ", os.PendingRebootReasons)})" : "No",
                Severity: os.PendingReboot ? Severity.Warning : Severity.Ok));
        }

        var u = scan.Updates;
        if (u is null)
        {
            return pending.Contains(Sections.Updates)
                ? new CardModel { Title = "Support and updates", State = "Reading...", Fields = fields }
                : new CardModel { Title = "Support and updates", Fields = fields, Notes = Cards.Notes(scan, Sections.Updates) };
        }

        var quality = u.LastQualityUpdate;
        fields.Add(new("Last monthly update", quality is null ? "None found in the history" : $"{Formatting.Date(quality.Date)} ({(int)(scan.StartedAt - quality.Date).TotalDays} days ago)",
            Severity: quality is null || (scan.StartedAt - quality.Date).TotalDays > 45 ? Severity.Warning : Severity.Ok));
        if (quality is not null)
        {
            fields.Add(new("Update", quality.Title));
        }

        fields.Add(new("Last update of any kind", u.LastInstalled is { } last ? Formatting.DateTime(last.Date) : "?"));
        fields.Add(new("Update service", $"{u.ServiceStartMode ?? "?"} · {u.ServiceState ?? "?"}",
            Severity: string.Equals(u.ServiceStartMode, "Disabled", StringComparison.OrdinalIgnoreCase) ? Severity.Warning : null));

        return new CardModel
        {
            Title = "Support and updates",
            Fields = fields,
            Lines = u.RecentFailures.Select(f => new StatusLine(Severity.Info, $"Failed: {f.Title}", Formatting.DateTime(f.Date))).ToList(),
            Tables =
            [
                new("Recent updates", ["Date", "Update", "Result"],
                    // Each update once (Defender definitions install several times a day), newest first.
                    u.History.DistinctBy(h => (h.Title, h.Succeeded, h.IsUninstall)).Take(15).Select(h => (IReadOnlyList<string>)
                    [
                        Formatting.Date(h.Date), h.Title, h.IsUninstall ? "Uninstalled" : h.Succeeded ? "Installed" : "Failed",
                    ]).ToList(),
                    "No update history."),
            ],
            Notes = [.. Cards.Notes(scan, Sections.Updates), $"End-of-support dates: {(Lifecycle.Current.Source == "built in" ? "Unravel's built-in table" : Lifecycle.Current.Source)}."],
        };
    }

    private static CardModel Stability(ScanResult scan, ICollection<string> pending)
    {
        if (scan.Stability is not { } s)
        {
            return Cards.Pending("Stability", Sections.Stability, pending, scan);
        }

        var now = scan.StartedAt;
        string Count(IReadOnlyList<StabilityEvent> events) => $"{s.InLast(events, 7, now)} in 7 days · {events.Count} in {s.Days} days";
        var bursts = s.DiskErrorBursts;

        // Newest events of each kind (disk errors as bursts), newest first.
        var events = s.BlueScreens.Take(10).Select(e => (e.Time, Kind: "Blue screen", e.Detail))
            .Concat(s.UnexpectedShutdowns.Take(10).Select(e => (e.Time, Kind: "Unexpected shutdown", e.Detail)))
            .Concat(bursts.Take(10).Select(b => (Time: b.Start, Kind: "Disk", Detail: b.Count == 1 ? b.Detail : $"{b.Detail} ({b.Count} events)")))
            .Concat(s.HardwareErrors.Take(10).Select(e => (e.Time, Kind: "Hardware (WHEA)", e.Detail)))
            .OrderByDescending(e => e.Time)
            .Take(20)
            .ToList();

        var memory = s.LastMemoryTest;
        return new CardModel
        {
            Title = "Stability",
            Subtitle = $"Last {s.Days} days",
            Actions =
            [
                new PageAction("Open Reliability Monitor", PageActionKind.OpenWindowsTool, "perfmon.exe", "/rel",
                    "Windows' own day-by-day view of crashes, failed updates and errors, and what lowered the score"),
            ],
            Fields =
            [
                new("Blue screens", Count(s.BlueScreens), Severity: s.BlueScreens.Count switch { 0 => Severity.Ok, 1 => Severity.Info, _ => Severity.Warning }),
                new("Unexpected shutdowns", Count(s.UnexpectedShutdowns), Severity: s.UnexpectedShutdowns.Count switch { 0 => Severity.Ok, 1 => Severity.Info, _ => Severity.Warning }),
                new("Disk errors", s.DiskErrors.Count == 0 ? "None" : $"{s.DiskErrors.Count} events in {bursts.Count} burst{(bursts.Count == 1 ? "" : "s")}",
                    Severity: s.DiskErrors.Count == 0 ? Severity.Ok : s.DiskErrors.Any(e => !e.IsRetryOnly) || bursts.Count >= 3 ? Severity.Warning : Severity.Info),
                new("Hardware errors (WHEA)", Count(s.HardwareErrors), Severity: s.HardwareErrors.Count == 0 ? Severity.Ok : Severity.Warning),
                new("App crashes", s.AppCrashes.Count == 0 ? "None" : $"{s.AppCrashes.Sum(c => c.Count)} in {s.AppCrashes.Count} app{(s.AppCrashes.Count == 1 ? "" : "s")}"),
                new("Crash dumps", s.Minidumps is null ? "Needs administrator" : s.Minidumps.Count == 0 ? "None" : $"{s.Minidumps.Count} in C:\\Windows\\Minidump"),
                new("Last memory test", memory is null ? "Never run (or no result in the log)" : $"{Formatting.Date(memory.Time)}: {(memory.ErrorsFound ? "errors found" : "no errors")}",
                    Severity: memory is null ? null : memory.ErrorsFound ? Severity.Critical : Severity.Ok),
                new("Reliability index", s.Reliability.Count == 0 ? "Not recorded on this PC" : ReliabilityText(s.Reliability[^1], now),
                    Severity: s.Reliability.Count == 0 ? null : s.Reliability[^1].Index switch { >= 7 => Severity.Ok, >= 4 => Severity.Info, _ => Severity.Warning }),
            ],
            Chart = s.Reliability.Count > 1
                ? new ChartModel("Reliability Monitor (1-10)", s.Reliability.Select(p => p.Index).ToList(), 1, 10,
                    s.Reliability[0].Day.ToString("d MMM", Culture), s.Reliability[^1].Day.ToString("d MMM", Culture))
                : null,
            Tables =
            [
                .. events.Count == 0 ? Array.Empty<TableModel>() :
                [
                    new TableModel("Recent events", ["When", "Kind", "Event"],
                        events.Select(e => (IReadOnlyList<string>)[Formatting.DateTime(e.Time), e.Kind, e.Detail]).ToList()),
                ],
                .. s.AppCrashes.Count == 0 ? Array.Empty<TableModel>() :
                [
                    new TableModel("App crashes", ["App", "Crashes", "Last crash", "Faulting module"],
                        s.AppCrashes.Take(10).Select(c => (IReadOnlyList<string>)[c.App, c.Count.ToString(Culture), Formatting.DateTime(c.LastCrash), c.Module ?? ""]).ToList()),
                ],
            ],
            Notes = Cards.Notes(scan, Sections.Stability),
        };
    }

    /// <summary>"5.9 of 10 today", or with the date when Windows has not scored today yet.</summary>
    private static string ReliabilityText(ReliabilityPoint latest, DateTimeOffset now)
    {
        var score = latest.Index.ToString("0.0", Culture);
        return latest.Day == DateOnly.FromDateTime(now.LocalDateTime)
            ? $"{score} of 10 today"
            : $"{score} of 10 (latest score, {latest.Day.ToString("d MMM", Culture)})";
    }
}

using System.Globalization;
using System.Text;
using Unravel.Core.Collectors;
using Unravel.Core.Diagnostics;
using Unravel.Core.Infrastructure;
using Unravel.Core.Models;
using Unravel.Core.Reports;
using Unravel.Core.Summary;

namespace Unravel.Core.Rmm;

/// <summary>
/// Options for <c>Unravel.exe --rmm</c>, the no-window mode the N-sight script (rmm/Unravel-NSight.ps1) runs as Local
/// System, as Unveil's does. Unknown options are reported, never guessed.
/// </summary>
public sealed record RmmOptions
{
    /// <summary>Default folder for reports: the same place on every PC, so the tech can find them.</summary>
    public static string DefaultReportFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Unravel", "Reports");

    public string ReportFolder { get; init; } = DefaultReportFolder;

    /// <summary>File the one-line result is written to (the script prints it for N-sight).</summary>
    public string? ResultFile { get; init; }

    /// <summary>How many runs' reports to keep in <see cref="ReportFolder"/>.</summary>
    public int Keep { get; init; } = 30;

    /// <summary>Redact serials, names, addresses and IDs in the reports (Reports page's "Redact sensitive fields").</summary>
    public bool Redact { get; init; }

    /// <summary>Also save the software list as CSV.</summary>
    public bool SoftwareCsv { get; init; }

    /// <summary>Shown at the top of the report, as on the Reports page.</summary>
    public string? Organisation { get; init; }

    public string? Error { get; init; }

    public static RmmOptions Parse(IReadOnlyList<string> args)
    {
        var options = new RmmOptions();
        string? error = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? Value() => i + 1 < args.Count ? args[++i] : null;

            var next = arg.ToLowerInvariant() switch
            {
                "--rmm" => options,
                "--out" => Value() is { Length: > 0 } folder ? options with { ReportFolder = folder } : options with { Error = "--out needs a folder." },
                "--result" => Value() is { Length: > 0 } file ? options with { ResultFile = file } : options with { Error = "--result needs a file name." },
                "--keep" => int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var k) && k >= 1
                    ? options with { Keep = k }
                    : options with { Error = "--keep needs a number of reports (1 or more)." },
                "--redact" => options with { Redact = true },
                "--csv" => options with { SoftwareCsv = true },
                "--organisation" => Value() is { Length: > 0 } name ? options with { Organisation = name } : options with { Error = "--organisation needs a name." },
                _ => options with { Error = $"Unknown option: {arg}" },
            };

            // Keep reading after a mistake, so --result is still known and the error reaches N-sight.
            error ??= next.Error;
            options = next with { Error = null };
        }

        return options with { Error = error };
    }
}

/// <summary>
/// Exit codes for N-sight: 0 is a pass; N-able asks for codes above 1000 for a failure, so the text shows on the
/// dashboard. The script adds 1011 (no download, no copy) and 1012 (blocked or timed out).
/// </summary>
public static class RmmExitCodes
{
    public const int Pass = 0;
    public const int Warnings = 1001;
    public const int Critical = 1002;
    public const int Error = 1010;

    /// <summary>Only information or nothing passes; a warning or a critical finding fails, so it can raise an alert.</summary>
    public static int For(IEnumerable<Finding> findings) =>
        findings.Select(f => f.Severity).DefaultIfEmpty(Severity.Ok).Max() switch
        {
            Severity.Critical => Critical,
            Severity.Warning => Warnings,
            _ => Pass,
        };
}

/// <summary>The one line N-sight shows (it keeps only the first 255 characters of a script's output).</summary>
public static class RmmSummary
{
    public const int MaxLength = 255;

    public static string Line(ScanResult scan, IReadOnlyList<Finding> findings, string? reportFileName)
    {
        var critical = findings.Count(f => f.Severity == Severity.Critical);
        var warnings = findings.Count(f => f.Severity == Severity.Warning);
        var counts = string.Join(", ", new[]
        {
            critical > 0 ? $"{critical} critical" : null,
            warnings > 0 ? $"{warnings} warning{(warnings == 1 ? "" : "s")}" : null,
        }.OfType<string>());

        var parts = new List<string>
        {
            critical > 0 ? $"Needs action: {counts}." : warnings > 0 ? $"Worth a look: {counts}." : "OK: no problems found.",
        };

        if (critical + warnings > 0)
        {
            // The problems themselves, most serious first, as far as they fit.
            parts.AddRange(findings.Where(f => f.Severity >= Severity.Warning).OrderByDescending(f => f.Severity)
                .Select(f => $"{f.Area}: {f.Message.TrimEnd('.')}."));
        }
        else
        {
            // Nothing wrong: what the PC is, so the dashboard still says something useful.
            var summary = SummaryBuilder.Build(scan);
            var specs = summary.Specs.Where(s => s.State == TileState.Ready && s.Label != "Uptime").Select(s => s.Value);
            parts.Add(string.Join("; ", new[] { summary.Title }.Concat(specs)) + ".");
        }

        return Fit(parts, reportFileName is null ? null : $"Report: {reportFileName}");
    }

    /// <summary>Adds parts while they fit, keeping room for <paramref name="tail"/>; the first part is always kept (cut if it must be).</summary>
    internal static string Fit(IReadOnlyList<string> parts, string? tail)
    {
        var room = MaxLength - (tail is null ? 0 : tail.Length + 1);
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            var next = sb.Length == 0 ? part : " " + part;
            if (sb.Length + next.Length <= room)
            {
                sb.Append(next);
            }
            else if (sb.Length == 0)
            {
                sb.Append(part[..Math.Min(part.Length, Math.Max(room, 0))]);
            }
        }

        if (tail is not null && sb.Length + tail.Length + 1 <= MaxLength)
        {
            sb.Append(' ').Append(tail);
        }

        return sb.Length > MaxLength ? sb.ToString(0, MaxLength) : sb.ToString();
    }
}

/// <summary>Keeps the report folder from growing forever when the check runs every day.</summary>
public static class ReportRetention
{
    private static readonly string[] Extensions = [".html", ".json", ".csv"];

    /// <summary>Deletes all but the newest <paramref name="keep"/> runs (each run's files share one name).</summary>
    public static int Prune(string folder, int keep)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        var old = Directory.EnumerateFiles(folder, "Unravel_*.*")
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            // "Unravel_..._software.csv" belongs to the run "Unravel_...".
            .GroupBy(f => RunName(Path.GetFileNameWithoutExtension(f)), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Max(File.GetLastWriteTimeUtc))
            .Skip(Math.Max(1, keep))
            .SelectMany(g => g)
            .ToList();

        var deleted = 0;
        foreach (var file in old)
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Try again next run.
            }
        }

        return deleted;
    }

    private static string RunName(string name) =>
        name.EndsWith(SoftwareSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^SoftwareSuffix.Length] : name;

    internal const string SoftwareSuffix = "_software";
}

/// <summary>Runs the whole scan with no window, for N-sight. Never throws: failures become <see cref="RmmExitCodes.Error"/>.</summary>
public static class RmmRunner
{
    public sealed record Outcome(int ExitCode, string Line, string? HtmlPath);

    public static Outcome Run(RmmOptions options, Func<CollectContext>? context = null)
    {
        try
        {
            if (options.Error is not null)
            {
                return Finish(options, new Outcome(RmmExitCodes.Error, $"Unravel: {options.Error}", null));
            }

            var ctx = (context ?? CollectContext.LiveForRmm)();
            var scan = ScanRunner.CreateDefault().RunAsync(ctx).ConfigureAwait(false).GetAwaiter().GetResult();
            var doc = ReportDocument.Create(scan, new ReportOptions { Organisation = options.Organisation, Redact = options.Redact }, now: ctx.Now);

            // The findings and the line come from the report's (possibly redacted) scan, so the line never shows
            // more than the report does.
            var findings = HealthRules.Evaluate(doc.Scan).ToList();

            Directory.CreateDirectory(options.ReportFolder);
            // Time (to the second) in the name: the check can run more than once a day, or twice in a minute.
            var stem = $"{doc.FileStem}_{doc.GeneratedAt.ToString("HHmmss", CultureInfo.InvariantCulture)}";
            var htmlPath = Path.Combine(options.ReportFolder, stem + ".html");
            HtmlReport.Write(doc, htmlPath);
            DataExport.WriteJson(doc, Path.ChangeExtension(htmlPath, ".json"));
            if (options.SoftwareCsv)
            {
                DataExport.WriteSoftwareCsv(doc, Path.Combine(options.ReportFolder, stem + ReportRetention.SoftwareSuffix + ".csv"));
            }

            ReportRetention.Prune(options.ReportFolder, options.Keep);
            return Finish(options, new Outcome(RmmExitCodes.For(findings), RmmSummary.Line(doc.Scan, findings, Path.GetFileName(htmlPath)), htmlPath));
        }
#pragma warning disable CA1031 // Top level of the no-window mode: report any failure as a result line and an exit code.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // The message can hold a path with the user's name in it: scrub it like a redacted report.
            return Finish(options, new Outcome(RmmExitCodes.Error, $"Unravel failed: {ex.GetType().Name}: {Redactor.For(new ScanResult()).Text(ex.Message)}", null));
        }
    }

    /// <summary>Writes the result line where the script can read it.</summary>
    private static Outcome Finish(RmmOptions options, Outcome outcome)
    {
        var line = outcome.Line.Length > RmmSummary.MaxLength ? outcome.Line[..RmmSummary.MaxLength] : outcome.Line;
        try
        {
            if (options.ResultFile is { } file)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
                File.WriteAllText(file, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The exit code still tells N-sight the result.
        }

        return outcome with { Line = line };
    }
}

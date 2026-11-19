using System.Globalization;
using Unravel.Core.Diagnostics;
using Unravel.Core.Models;

namespace Unravel.Core.Reports;

/// <summary>What the technician adds to a report (plan.md section 6), and whether to redact it for a third party (section 8).</summary>
public sealed record ReportOptions
{
    public string? ClientName { get; init; }

    public string? TicketNumber { get; init; }

    public string? TechnicianNotes { get; init; }

    /// <summary>Hide serial numbers, names, addresses and IDs (see <see cref="Redactor"/>).</summary>
    public bool Redact { get; init; }
}

/// <summary>An active test run in this session (Diagnostics page), so its result goes into the report.</summary>
public sealed record ActiveTestRecord(string Test, DateTimeOffset RanAt, TestOutcome Outcome);

/// <summary>
/// Everything one report contains. The HTML, PDF, JSON and CSV exports are all rendered from this, so they agree with
/// each other, and with redaction on, none of them holds the sensitive values.
/// </summary>
public sealed record ReportDocument
{
    /// <summary>Bump when the JSON shape changes in a way that breaks reading older files.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>"Unravel 0.4.0".</summary>
    public string Generator { get; init; } = DefaultGenerator;

    public DateTimeOffset GeneratedAt { get; init; }

    public string? ClientName { get; init; }

    public string? TicketNumber { get; init; }

    public string? TechnicianNotes { get; init; }

    public bool Redacted { get; init; }

    /// <summary>The scan, already redacted when <see cref="Redacted"/> is set.</summary>
    public required ScanResult Scan { get; init; }

    public IReadOnlyList<ActiveTestRecord> ActiveTests { get; init; } = [];

    public static ReportDocument Create(ScanResult scan, ReportOptions options, IEnumerable<ActiveTestRecord>? tests = null, DateTimeOffset? now = null)
    {
        var redactor = options.Redact ? Redactor.For(scan) : null;
        var records = (tests ?? []).OrderBy(t => t.RanAt).ToList();
        return new ReportDocument
        {
            GeneratedAt = now ?? DateTimeOffset.Now,
            ClientName = Clean(options.ClientName),
            TicketNumber = Clean(options.TicketNumber),
            TechnicianNotes = Clean(options.TechnicianNotes),
            Redacted = redactor is not null,
            Scan = redactor?.Redact(scan) ?? scan,
            ActiveTests = redactor is null ? records : records.Select(redactor.Redact).ToList(),
        };
    }

    public static string DefaultGenerator { get; } =
        $"Unravel {typeof(ReportDocument).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";

    /// <summary>
    /// "Unravel_DESKTOP-1AB2C3D_PF3ABC12_2026-11-19" (plan.md section 6). A redacted report is named after the model
    /// instead, so the file name does not give away what the report hides.
    /// </summary>
    public string FileStem
    {
        get
        {
            var date = GeneratedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var device = Scan.Device;
            var parts = Redacted
                ? new[] { device?.DisplayName ?? "PC" }
                : new[] { device?.ComputerName ?? "PC", device?.SerialNumber };
            return string.Join('_', new[] { "Unravel" }.Concat(parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Safe(p!))).Append(date));
        }
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>Only letters, digits, '-' and '.', so the name is valid everywhere and has no '_' of its own.</summary>
    private static string Safe(string value)
    {
        var chars = value.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '-').ToArray();
        var text = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return text.Length > 40 ? text[..40].TrimEnd('-') : text.Length == 0 ? "PC" : text;
    }
}

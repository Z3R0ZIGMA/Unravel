using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Unravel.Core.Diagnostics;
using Unravel.Core.Models;

namespace Unravel.Core.Reports;

/// <summary>
/// The JSON export (plan.md section 6): every section's data as read, plus the findings, Windows 11 readiness and
/// support status, in a versioned shape so the same PC can be compared over time. And the software list as CSV.
/// </summary>
public static class DataExport
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string Json(ReportDocument doc)
    {
        var scan = doc.Scan;
        var data = new
        {
            doc.SchemaVersion,
            doc.Generator,
            doc.GeneratedAt,
            doc.Organisation,
            doc.User,
            doc.Notes,
            doc.Redacted,
            Scan = new
            {
                scan.StartedAt,
                DurationSeconds = Math.Round(scan.Duration.TotalSeconds, 1),
                RanAsAdministrator = scan.IsAdmin,
            },
            Findings = HealthRules.Evaluate(scan).OrderByDescending(f => f.Severity).ToList(),
            Windows11 = scan.Cpu is null && scan.Firmware is null ? null : Windows11(Win11Check.Evaluate(scan)),
            WindowsSupport = scan.Os is { } os ? Lifecycle.Current.Check(os, scan.StartedAt) : null,
            doc.ActiveTests,
            // Section name -> what was read. Data is written with its own type, so every field is there.
            Sections = scan.Results.ToDictionary(r => r.Section, r => new
            {
                Data = r.Data,
                r.Warnings,
                r.NeedsAdmin,
                DurationSeconds = Math.Round(r.Duration.TotalSeconds, 2),
            }),
        };
        return JsonSerializer.Serialize(data, JsonOptions);
    }

    private static object Windows11(Win11Readiness r) => new { r.State, r.Summary, r.AlreadyWindows11, r.Checks };

    public static void WriteJson(ReportDocument doc, string path) => File.WriteAllText(path, Json(doc), Utf8NoBom);

    /// <summary>
    /// Installed programs and Store apps (not the hidden system components or Store framework packages, as on the
    /// Software page by default). UTF-8 with a BOM so Excel reads names with accents correctly.
    /// </summary>
    public static string SoftwareCsv(ReportDocument doc)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Name,Version,Publisher,Installed,Size (MB),Architecture,Scope,Source,Remote access tool");
        var software = doc.Scan.Software;
        var entries = software is null ? [] : software.Visible.Concat(software.VisibleStoreApps)
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var e in entries)
        {
            csv.AppendJoin(',', new[]
            {
                e.Name, e.Version, e.Publisher, e.InstallDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                e.SizeBytes is { } b ? (b / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) : null,
                e.Architecture, e.Scope, e.Source, e.RemoteAccessTool,
            }.Select(Cell)).AppendLine();
        }

        return csv.ToString();
    }

    public static void WriteSoftwareCsv(ReportDocument doc, string path) => File.WriteAllText(path, SoftwareCsv(doc), new UTF8Encoding(true));

    /// <summary>Quoted when needed; a leading =, +, - or @ is prefixed so Excel does not run it as a formula.</summary>
    internal static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (value[0] is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;
    }
}

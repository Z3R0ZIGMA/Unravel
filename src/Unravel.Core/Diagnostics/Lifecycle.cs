using System.Globalization;
using System.Text.Json;
using Unravel.Core.Models;

namespace Unravel.Core.Diagnostics;

public enum SupportState
{
    Supported,
    EndingSoon,
    OutOfSupport,
    /// <summary>Newer than every release in the table: assumed supported.</summary>
    NewerThanTable,
    Unknown,
}

public sealed record SupportStatus(SupportState State, DateOnly? EndDate, string Text, string? Note = null);

/// <summary>
/// End-of-support check (plan.md section 4.4): the Windows build and edition against a lifecycle table bundled in the
/// app (Diagnostics/lifecycle.json), which a lifecycle.json next to Unravel.exe can replace.
/// </summary>
public sealed class Lifecycle
{
    public const string FileName = "lifecycle.json";

    private readonly List<Release> _releases;

    private Lifecycle(List<Release> releases, string source)
    {
        _releases = releases;
        Source = source;
    }

    /// <summary>"built in" or the path of the override file.</summary>
    public string Source { get; }

    /// <summary>The table the rules use. The app replaces it at start when an override file exists.</summary>
    public static Lifecycle Current { get; set; } = BuiltIn();

    public static Lifecycle BuiltIn()
    {
        using var stream = typeof(Lifecycle).Assembly.GetManifestResourceStream("Unravel.lifecycle.json")
                           ?? throw new InvalidOperationException("The built-in lifecycle table is missing.");
        return new Lifecycle(Parse(stream), "built in");
    }

    /// <summary>Uses <paramref name="folder"/>\lifecycle.json when it exists and is valid, else the built-in table.</summary>
    public static Lifecycle Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path))
        {
            return BuiltIn();
        }

        try
        {
            using var stream = File.OpenRead(path);
            var releases = Parse(stream);
            return releases.Count > 0 ? new Lifecycle(releases, path) : BuiltIn();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or FormatException or KeyNotFoundException)
        {
            return BuiltIn();
        }
    }

    public SupportStatus Check(OsInfo os, DateTimeOffset now)
    {
        if (os.Build is not { } build)
        {
            return new(SupportState.Unknown, null, "Unknown (Windows build not read)");
        }

        var ltsc = os.EditionId?.Contains("EnterpriseS", StringComparison.OrdinalIgnoreCase) == true;
        var enterprise = os.EditionId is { } e && (e.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase)
                                                   || e.StartsWith("Education", StringComparison.OrdinalIgnoreCase)
                                                   || e.StartsWith("IoTEnterprise", StringComparison.OrdinalIgnoreCase));

        var family = _releases.Where(r => ltsc ? r.Ltsc is not null : r.Ltsc is null).ToList();
        var release = family.FirstOrDefault(r => r.Build == build);
        if (release is null)
        {
            if (family.Count > 0 && build > family.Max(r => r.Build))
            {
                return new(SupportState.NewerThanTable, null, "Supported (newer than Unravel's lifecycle table)");
            }

            return ltsc || family.Count == 0 || build >= family.Min(r => r.Build)
                ? new(SupportState.Unknown, null, $"Unknown (build {build} is not in the lifecycle table)")
                : new(SupportState.OutOfSupport, null, $"Out of support (build {build} is older than every release in the table)");
        }

        var end = ltsc ? release.Ltsc : enterprise ? release.Enterprise : release.HomePro;
        if (end is not { } endDate)
        {
            return new(SupportState.Unknown, null, "Unknown");
        }

        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var date = endDate.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        var label = $"{release.Product} {release.Version}";
        if (endDate < today)
        {
            return new(SupportState.OutOfSupport, endDate, $"Out of support since {date} ({label})", release.Note);
        }

        var days = endDate.DayNumber - today.DayNumber;
        return days <= 90
            ? new(SupportState.EndingSoon, endDate, $"Supported until {date}: ends in {days} days ({label})", release.Note)
            : new(SupportState.Supported, endDate, $"Supported until {date} ({label})", release.Note);
    }

    private sealed record Release(string Product, string Version, int Build, DateOnly? HomePro, DateOnly? Enterprise, DateOnly? Ltsc, string? Note);

    private static List<Release> Parse(Stream stream)
    {
        using var doc = JsonDocument.Parse(stream);
        var list = new List<Release>();
        foreach (var r in doc.RootElement.GetProperty("releases").EnumerateArray())
        {
            string? Text(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            DateOnly? Date(string name) => Text(name) is { } s ? DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
            list.Add(new Release(Text("product") ?? "Windows", Text("version") ?? "?", r.GetProperty("build").GetInt32(),
                Date("homePro"), Date("enterprise"), Date("ltsc"), Text("note")));
        }

        return list;
    }
}

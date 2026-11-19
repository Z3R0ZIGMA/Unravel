using System.Text.Json;
using Unravel.Core.Diagnostics;
using Unravel.Core.Models;
using Unravel.Core.Reports;

namespace Unravel.Core.Tests;

/// <summary>Phase 4: the report document, redaction, and the HTML, JSON and CSV exports.</summary>
public class Phase4Tests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Fixtures =>
    [
        "asus-expertbook-b1403cva.json", "dell-hybrid-joined-problems.json", "hyperv-vm-limited.json", "whitebox-desktop-problems.json",
    ];

    private static async Task<ScanResult> Scan(string fixture) => await FixtureScanTests.Scan(fixture);

    /// <summary>Every value redaction must remove, read from the un-redacted scan.</summary>
    private static List<string> SensitiveValues(ScanResult scan)
    {
        var values = new List<string?>
        {
            scan.Device?.SerialNumber, scan.Device?.Uuid, scan.Device?.ComputerName, scan.Firmware?.BoardSerial,
            scan.Identity?.TenantId, scan.Identity?.DeviceId, scan.Identity?.MdmUser, scan.Software?.Office?.Account,
            scan.Network?.Wifi?.Ssid,
        };
        values.AddRange(scan.Storage?.Disks.Select(d => d.SerialNumber) ?? []);
        foreach (var a in scan.Network?.Adapters ?? [])
        {
            values.Add(a.MacAddress);
            values.AddRange(a.IPv4.Concat(a.Gateways).Concat(a.DnsServers));
        }

        if (scan.Device is { PartOfDomain: true } d)
        {
            values.Add(d.Domain);
        }

        return values.OfType<string>().Select(v => v.Trim()).Where(v => v.Length >= 3).Distinct().ToList();
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Redaction_RemovesSensitiveValuesFromEveryExport(string fixture)
    {
        var scan = await Scan(fixture);
        var sensitive = SensitiveValues(scan);
        var doc = ReportDocument.Create(scan, new ReportOptions { Redact = true }, now: Now);

        Assert.True(doc.Redacted);
        // The logo's base64 could match a short value by chance: leave it out of the search.
        var html = System.Text.RegularExpressions.Regex.Replace(HtmlReport.Render(doc), "src=\"data:image/png;base64,[^\"]+\"", "");
        foreach (var (name, text) in new[] { ("html", html),("json", DataExport.Json(doc)), ("csv", DataExport.SoftwareCsv(doc)) })
        {
            foreach (var value in sensitive)
            {
                Assert.False(text.Contains(value, StringComparison.OrdinalIgnoreCase), $"{fixture} {name} still contains \"{value}\"");
                Assert.False(text.Contains(JsonEncodedText.Encode(value).ToString(), StringComparison.OrdinalIgnoreCase), $"{fixture} {name} still contains \"{value}\" (escaped)");
            }
        }

        Assert.DoesNotContain(sensitive, v => doc.FileStem.Contains(v, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Redaction_KeepsEverythingElse(string fixture)
    {
        var scan = await Scan(fixture);
        var redacted = Redactor.For(scan).Redact(scan);

        Assert.Equal(scan.Results.Select(r => (r.Section, Type: r.Data?.GetType(), r.Warnings.Count, r.NeedsAdmin)),
            redacted.Results.Select(r => (r.Section, Type: r.Data?.GetType(), r.Warnings.Count, r.NeedsAdmin)));
        Assert.Equal(scan.Cpu?.Primary?.Name, redacted.Cpu?.Primary?.Name);
        Assert.Equal(scan.Memory?.InstalledBytes, redacted.Memory?.InstalledBytes);
        Assert.Equal(scan.Os?.Build, redacted.Os?.Build);
        Assert.Equal(scan.Software?.Programs.Count, redacted.Software?.Programs.Count);
        // The same health findings, so the redacted report tells the same story.
        Assert.Equal(HealthRules.Evaluate(scan).Select(f => (f.Severity, f.Area)), HealthRules.Evaluate(redacted).Select(f => (f.Severity, f.Area)));
    }

    [Fact]
    public async Task Redaction_DellFixture_ShowsWhatWasHidden()
    {
        var scan = await Scan("dell-hybrid-joined-problems.json");
        var redacted = Redactor.For(scan).Redact(scan);

        Assert.Equal(Redactor.Mark, redacted.Device!.SerialNumber);
        Assert.Equal(Redactor.Mark, redacted.Device.ComputerName);
        Assert.Equal(Redactor.Mark, redacted.Identity!.TenantId);
        Assert.All(redacted.Network!.Adapters.SelectMany(a => a.IPv4), ip => Assert.Equal(Redactor.Mark, ip));
        // A built-in account keeps its name; only the computer part goes.
        Assert.Contains(redacted.Identity.LocalAdmins!, a => a.Name == $@"{Redactor.Mark}\Administrator");
        Assert.Equal("Hybrid Entra joined", redacted.Identity.JoinType);
    }

    [Theory]
    [InlineData(@"C:\Users\jane.doe\AppData\Local\app.exe", @"C:\Users\[redacted]\AppData\Local\app.exe")]
    [InlineData(@"C:\Users\Public\Desktop", @"C:\Users\Public\Desktop")]
    [InlineData("S-1-5-21-1004336348-1177238915-682003330-1001", "S-1-5-21-[redacted]")]
    [InlineData("Adapter A0:B1:C2:D3:E4:F5 is up", "Adapter [redacted] is up")]
    [InlineData("Signed in as alex@contoso.com", "Signed in as [redacted]")]
    [InlineData("PC10 is not PC1", "PC10 is not [redacted]")]
    [InlineData("Version 10.0.26100.1", "Version 10.0.26100.1")]
    public void Redactor_Text(string input, string expected)
    {
        var scan = new ScanResult
        {
            Results = [new Collectors.CollectorResult { Section = Sections.Device, Data = new DeviceInfo { ComputerName = "PC1" } }],
        };
        Assert.Equal(expected, Redactor.For(scan).Text(input));
    }

    [Fact]
    public async Task Html_HasTheReportAndIsSelfContained()
    {
        var scan = await Scan("dell-hybrid-joined-problems.json");
        var doc = ReportDocument.Create(scan, new ReportOptions
        {
            Organisation = "Contoso Ltd", User = " Jane Doe ", Notes = "Replaced fan.\n<b>not bold</b>",
        }, [new ActiveTestRecord("Disk speed", Now, new TestOutcome(Severity.Ok, "Read 3,000 MB/s", ["Write 1,500 MB/s"]))], Now);
        var html = HtmlReport.Render(doc);

        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        Assert.Contains("TESTTAG", html, StringComparison.Ordinal);
        Assert.Contains("Contoso Ltd", html, StringComparison.Ordinal);
        Assert.Contains("Jane Doe</span>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;not bold&lt;/b&gt;", html, StringComparison.Ordinal);
        Assert.Contains("Read 3,000 MB/s", html, StringComparison.Ordinal);
        foreach (var section in new[] { "Device", "Hardware", "Windows", "Security", "Software", "Diagnostics", "Health checks", "Notes:" })
        {
            Assert.Contains($">{section}</h2>", html, StringComparison.Ordinal);
        }

        Assert.Contains("Organisation:</span> Contoso Ltd", html, StringComparison.Ordinal);
        Assert.Contains("User:</span> Jane Doe", html, StringComparison.Ordinal);

        // The logo is inside the file; no scripts, and nothing loaded from elsewhere.
        Assert.Contains("<img class=\"logo\" src=\"data:image/png;base64,iVBOR", html, StringComparison.Ordinal);
        var withoutLogo = System.Text.RegularExpressions.Regex.Replace(html, "src=\"data:image/png;base64,[^\"]+\"", "");
        Assert.DoesNotContain("<script", withoutLogo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", withoutLogo, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", withoutLogo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Json_IsVersionedAndHasEverySection()
    {
        var scan = await Scan("dell-hybrid-joined-problems.json");
        var doc = ReportDocument.Create(scan, new ReportOptions { User = "Jane Doe" }, now: Now);
        using var json = JsonDocument.Parse(DataExport.Json(doc));
        var root = json.RootElement;

        Assert.Equal(ReportDocument.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Jane Doe", root.GetProperty("user").GetString());
        Assert.False(root.GetProperty("redacted").GetBoolean());
        Assert.True(root.GetProperty("findings").GetArrayLength() > 0);
        // Most serious first, with enums as words.
        Assert.Equal(HealthRules.Evaluate(scan).Max(f => f.Severity).ToString().ToLowerInvariant(),
            root.GetProperty("findings")[0].GetProperty("severity").GetString());
        var sections = root.GetProperty("sections");
        Assert.All(scan.Results, r => Assert.True(sections.TryGetProperty(r.Section, out _), r.Section));
        Assert.Equal("TESTTAG", sections.GetProperty(Sections.Device).GetProperty("data").GetProperty("serialNumber").GetString());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("windows11").ValueKind);
    }

    [Fact]
    public async Task Csv_ListsVisibleSoftware()
    {
        var scan = await Scan("dell-hybrid-joined-problems.json");
        var csv = DataExport.SoftwareCsv(ReportDocument.Create(scan, new ReportOptions(), now: Now));
        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("Name,Version,Publisher", lines[0], StringComparison.Ordinal);
        Assert.Equal(scan.Software!.Visible.Count() + scan.Software.VisibleStoreApps.Count(), lines.Length - 1);
        Assert.Contains(lines, l => l.StartsWith("Zoom Workplace (64-bit),6.1.0,\"Zoom Video Communications, Inc.\"", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("TeamViewer,", StringComparison.Ordinal) && l.EndsWith(",TeamViewer", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a, b", "\"a, b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("-5", "'-5")]
    [InlineData("\t=1+1", "'\t=1+1")]
    [InlineData("\r=1+1", "\"'\r=1+1\"")]
    [InlineData(null, "")]
    public void Csv_Cell(string? value, string expected) => Assert.Equal(expected, DataExport.Cell(value));

    [Fact]
    public async Task FileStem_FollowsThePlan()
    {
        var scan = await Scan("dell-hybrid-joined-problems.json");
        Assert.Equal("Unravel_LAPTOP-042_TESTTAG_2026-11-19", ReportDocument.Create(scan, new ReportOptions(), now: Now).FileStem);

        var redacted = ReportDocument.Create(scan, new ReportOptions { Redact = true }, now: Now).FileStem;
        Assert.StartsWith("Unravel_", redacted, StringComparison.Ordinal);
        Assert.EndsWith("_2026-11-19", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("TESTTAG", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("LAPTOP-042", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_BlankFieldsAreLeftOut()
    {
        var doc = ReportDocument.Create(new ScanResult(), new ReportOptions { Organisation = "  ", Notes = "" }, now: Now);
        Assert.Null(doc.Organisation);
        Assert.Null(doc.Notes);
        Assert.Equal("Unravel_PC_2026-11-19", doc.FileStem);
        Assert.Contains("<!doctype html>", HtmlReport.Render(doc), StringComparison.Ordinal);
    }
}

using Unravel.Core.Collectors;
using Unravel.Core.Diagnostics;
using Unravel.Core.Infrastructure;
using Unravel.Core.Models;
using Unravel.Core.Rmm;

namespace Unravel.Core.Tests;

/// <summary>Phase 6: the N-sight RMM mode (Unravel.exe --rmm).</summary>
public class Phase6Tests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static CollectContext Fixture(string name, bool asSystemNoUser = false)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        return new CollectContext(new FixtureSystemSource(SystemFixture.Load(path)), IsAdmin: true, Now)
        {
            AsSystem = asSystemNoUser,
            NoSignedInUser = asSystemNoUser,
        };
    }

    private static string TempFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "unravel-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void Options_Parse()
    {
        var o = RmmOptions.Parse(["--rmm", "--out", @"C:\R", "--result", @"C:\r.txt", "--keep", "5", "--redact", "--csv", "--organisation", "Contoso Ltd"]);
        Assert.Null(o.Error);
        Assert.Equal((@"C:\R", @"C:\r.txt", 5, true, true, "Contoso Ltd"), (o.ReportFolder, o.ResultFile, o.Keep, o.Redact, o.SoftwareCsv, o.Organisation));

        Assert.Equal(RmmOptions.DefaultReportFolder, RmmOptions.Parse(["--rmm"]).ReportFolder);
        Assert.EndsWith(@"Unravel\Reports", RmmOptions.DefaultReportFolder, StringComparison.OrdinalIgnoreCase);

        // A mistake is reported, and options after it (the result file) are still read so N-sight sees why.
        var bad = RmmOptions.Parse(["--rmm", "--bogus", "--result", @"C:\r.txt"]);
        Assert.Equal("Unknown option: --bogus", bad.Error);
        Assert.Equal(@"C:\r.txt", bad.ResultFile);
        Assert.Equal("--keep needs a number of reports (1 or more).", RmmOptions.Parse(["--keep", "0"]).Error);
    }

    [Fact]
    public void ExitCodes_FollowTheWorstFinding()
    {
        static Finding F(Severity s) => new(s, "Area", "Message");
        Assert.Equal(RmmExitCodes.Pass, RmmExitCodes.For([]));
        Assert.Equal(RmmExitCodes.Pass, RmmExitCodes.For([F(Severity.Info), F(Severity.Ok)]));
        Assert.Equal(RmmExitCodes.Warnings, RmmExitCodes.For([F(Severity.Info), F(Severity.Warning)]));
        Assert.Equal(RmmExitCodes.Critical, RmmExitCodes.For([F(Severity.Warning), F(Severity.Critical)]));
    }

    [Fact]
    public void Line_FitsN_SightAndKeepsTheReportName()
    {
        var scan = new ScanResult();
        var findings = Enumerable.Range(1, 20).Select(i => new Finding(Severity.Warning, $"Area {i}", new string('x', 40))).ToList();
        var line = RmmSummary.Line(scan, [new Finding(Severity.Critical, "Storage", "Drive C: is 97% full"), .. findings], "Unravel_PC_2026-11-19_0915.html");

        Assert.True(line.Length <= RmmSummary.MaxLength, line);
        Assert.StartsWith("Needs action: 1 critical, 20 warnings. Storage: Drive C: is 97% full.", line, StringComparison.Ordinal);
        Assert.EndsWith("Report: Unravel_PC_2026-11-19_0915.html", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Line_WhenNothingIsWrong_SaysWhatThePcIs()
    {
        var scan = await FixtureScanTests.Scan("asus-expertbook-b1403cva.json");
        var line = RmmSummary.Line(scan, [new Finding(Severity.Info, "Memory", "Single channel")], "r.html");
        Assert.StartsWith("OK: no problems found. ASUS EXPERTBOOK B1403CVA; 13th Gen Intel Core i5-1335U; 16 GB DDR5", line, StringComparison.Ordinal);
        Assert.EndsWith("Report: r.html", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Run_WritesTheReportsAndTheResult(bool redact)
    {
        var folder = TempFolder();
        try
        {
            var result = Path.Combine(folder, "last-result.txt");
            var options = new RmmOptions { ReportFolder = Path.Combine(folder, "Reports"), ResultFile = result, Redact = redact, SoftwareCsv = true };
            var outcome = RmmRunner.Run(options, () => Fixture("dell-hybrid-joined-problems.json"));

            Assert.Equal(RmmExitCodes.Warnings, outcome.ExitCode); // the Dell fixture's worst finding is a warning
            Assert.StartsWith("Worth a look:", outcome.Line, StringComparison.Ordinal);
            Assert.Equal(outcome.Line, File.ReadAllText(result).Trim());
            var files = Directory.GetFiles(options.ReportFolder).Select(Path.GetFileName).Order().ToList();
            Assert.Equal(3, files.Count);
            Assert.All(files, f => Assert.StartsWith("Unravel_", f, StringComparison.Ordinal));
            Assert.Contains(files, f => f!.EndsWith("_software.csv", StringComparison.Ordinal));

            // Redacted: neither the files' names, their contents nor the line give the serial or the computer away.
            var everything = string.Join("\n", files.Concat(Directory.GetFiles(options.ReportFolder).Select(File.ReadAllText)).Append(outcome.Line));
            Assert.Equal(!redact, everything.Contains("TESTTAG", StringComparison.Ordinal));
            Assert.Equal(!redact, everything.Contains("LAPTOP-042", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Run_ABadOptionFailsWithTheReason()
    {
        var folder = TempFolder();
        try
        {
            var options = RmmOptions.Parse(["--rmm", "--bogus", "--result", Path.Combine(folder, "r.txt")]);
            var outcome = RmmRunner.Run(options, () => throw new InvalidOperationException("must not scan"));
            Assert.Equal((RmmExitCodes.Error, "Unravel: Unknown option: --bogus"), (outcome.ExitCode, outcome.Line));
            Assert.Equal(outcome.Line, File.ReadAllText(Path.Combine(folder, "r.txt")).Trim());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Retention_KeepsTheNewestRuns()
    {
        var folder = TempFolder();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var stem = Path.Combine(folder, $"Unravel_PC_2026-11-1{i}_0900");
                foreach (var file in new[] { stem + ".html", stem + ".json", stem + "_software.csv" })
                {
                    File.WriteAllText(file, "x");
                    File.SetLastWriteTimeUtc(file, Now.UtcDateTime.AddDays(i));
                }
            }

            File.WriteAllText(Path.Combine(folder, "notes.txt"), "not ours");
            Assert.Equal(6, ReportRetention.Prune(folder, keep: 2));
            Assert.Equal(["Unravel_PC_2026-11-12_0900", "Unravel_PC_2026-11-13_0900"],
                Directory.GetFiles(folder, "*.html").Select(Path.GetFileNameWithoutExtension).Order());
            Assert.Equal(2, Directory.GetFiles(folder, "*_software.csv").Length);
            Assert.True(File.Exists(Path.Combine(folder, "notes.txt")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AsSystemWithNobodySignedIn_SaysPerUserDataIsLeftOut()
    {
        var scan = await ScanRunner.CreateDefault().RunAsync(Fixture("dell-hybrid-joined-problems.json", asSystemNoUser: true));
        Assert.Contains(scan.ResultFor(Sections.Startup)!.Warnings, w => w.StartsWith("Nobody is signed in", StringComparison.Ordinal));
        Assert.Contains(scan.ResultFor(Sections.Software)!.Warnings, w => w.StartsWith("Nobody is signed in", StringComparison.Ordinal));
        // dsregcmd's user state belongs to the system account then: unknown, not "No".
        Assert.Null(scan.Identity!.HasPrt);
        Assert.Null(scan.Identity.WorkplaceJoined);
    }

    [Fact]
    public void LiveSource_ForNobody_ReadsNoUserData()
    {
        var source = LiveSystemSource.ForUser(null);
        Assert.Empty(source.RegistryValues(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders"));
        Assert.Empty(source.Rows(RowKinds.StoreApps));
        // Machine-wide data is unaffected.
        Assert.NotEmpty(source.RegistryValues(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion"));
    }

    // ------------------------------------------------------------------ security review fixes (0.6.1)

    [Theory]
    [InlineData(@"C:\Program Files\App\app.exe", true)]
    [InlineData(@"c:/tools/x.exe", true)]
    [InlineData(@"\\attacker\share\x.exe", false)]
    [InlineData(@"\\?\UNC\attacker\share\x.exe", false)]
    [InlineData(@"\\?\C:\x.exe", false)]
    [InlineData(@"\\.\pipe\x", false)]
    [InlineData("//attacker/share/x.exe", false)]
    [InlineData(@"app.exe", false)]
    [InlineData(@"C:app.exe", false)]
    [InlineData("", false)]
    public void LocalPaths_RejectNetworkAndOddPaths(string path, bool local) => Assert.Equal(local, LocalPaths.IsLocal(path));

    [Fact]
    public void LiveSource_NeverOpensANetworkPathFromTheRegistry()
    {
        // A Run key pointing at another computer: no version is read, so Windows never connects to it.
        Assert.Empty(new LiveSystemSource().Rows(RowKinds.FileVersion, @"\\192.0.2.1\share\x.exe"));
    }

    [Theory]
    [InlineData(@"%LOCALAPPDATA%\App\app.exe /bg", @"C:\Users\jane\AppData\Local\App\app.exe /bg")]
    [InlineData(@"""%AppData%\x.exe""", @"""C:\Users\jane\AppData\Roaming\x.exe""")]
    [InlineData(@"%USERPROFILE%\x.exe", @"C:\Users\jane\x.exe")]
    [InlineData(@"%ProgramFiles%\x.exe", @"%ProgramFiles%\x.exe")]
    [InlineData(@"C:\x.exe", @"C:\x.exe")]
    public void Startup_UserVariablesPointIntoTheSignedInUsersProfile(string command, string expected) =>
        Assert.Equal(expected, StartupRules.ExpandForUser(command, @"C:\Users\jane"));

    [Fact]
    public void Run_ScrubsTheErrorLine()
    {
        var folder = TempFolder();
        try
        {
            var options = new RmmOptions { ReportFolder = folder };
            var outcome = RmmRunner.Run(options, () => throw new IOException(@"Access to C:\Users\jane.doe\AppData\x.json was denied for alex@contoso.com"));
            Assert.Equal(RmmExitCodes.Error, outcome.ExitCode);
            Assert.DoesNotContain("jane.doe", outcome.Line, StringComparison.Ordinal);
            Assert.DoesNotContain("alex@contoso.com", outcome.Line, StringComparison.Ordinal);
            Assert.StartsWith(@"Unravel failed: IOException: Access to C:\Users\[redacted]\AppData", outcome.Line, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Run_NamesReportsToTheSecond()
    {
        var folder = TempFolder();
        try
        {
            var outcome = RmmRunner.Run(new RmmOptions { ReportFolder = folder }, () => Fixture("dell-hybrid-joined-problems.json"));
            Assert.Matches(@"_\d{4}-\d{2}-\d{2}_\d{6}\.html$", outcome.HtmlPath!);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

using Unravel.Core.Collectors;
using Unravel.Core.Diagnostics;
using Unravel.Core.Infrastructure;
using Unravel.Core.Models;
using Unravel.Core.Pages;
using Unravel.Core.Summary;

namespace Unravel.Core.Tests;

/// <summary>Phase 3: stability, Windows Update, lifecycle, Windows 11 readiness, rules and the active tests' logic.</summary>
public class Phase3CollectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, object?> Event(int id, string provider, DateTimeOffset time, int level = 2, string? data0 = null, string? data3 = null) => new()
    {
        ["Id"] = (long)id, ["Provider"] = provider, ["Time"] = time.ToString("o"), ["Level"] = (long)level,
        ["Data0"] = data0, ["Data1"] = null, ["Data2"] = null, ["Data3"] = data3,
    };

    private static async Task<CollectorResult> Run(ICollector collector, SystemFixture fixture, bool admin = false) =>
        await collector.CollectAsync(new CollectContext(new FixtureSystemSource(fixture), admin, Now), CancellationToken.None);

    private static SystemFixture StabilityFixture()
    {
        var f = new SystemFixture();
        f.Rows[SystemFixture.RowKey(RowKinds.Events, StabilityCollector.SystemQuery)] =
        [
            Event(1001, "Microsoft-Windows-WER-SystemErrorReporting", Now.AddDays(-2), data0: "0x0000009f (0x0000000000000003, 0xffff)"),
            Event(1001, "Microsoft-Windows-WER-SystemErrorReporting", Now.AddDays(-10), data0: "0x00000124 (0x0, 0x0)"),
            Event(41, "Microsoft-Windows-Kernel-Power", Now.AddDays(-3), 1, data0: "0"),
            Event(41, "Microsoft-Windows-Kernel-Power", Now.AddDays(-10), 1, data0: "159"),
            Event(6008, "EventLog", Now.AddDays(-3)),
            // Two bursts of I/O retries (minutes apart within each), one real bad block.
            Event(153, "disk", Now.AddDays(-5), 3, data0: @"\Device\Harddisk0\DR0"),
            Event(153, "disk", Now.AddDays(-5).AddMinutes(2), 3, data0: @"\Device\Harddisk0\DR0"),
            Event(153, "disk", Now.AddDays(-5).AddMinutes(4), 3, data0: @"\Device\Harddisk0\DR0"),
            Event(153, "disk", Now.AddDays(-20), 3, data0: @"\Device\Harddisk0\DR0"),
            Event(7, "disk", Now.AddDays(-1), data0: @"\Device\Harddisk1\DR1"),
            Event(17, "Microsoft-Windows-WHEA-Logger", Now.AddDays(-4), 3),
            Event(18, "Microsoft-Windows-WHEA-Logger", Now.AddDays(-6), 2),
            // Outside the 30-day window: ignored even though the log returned it.
            Event(1001, "Microsoft-Windows-WER-SystemErrorReporting", Now.AddDays(-45), data0: "0x0000000a (0)"),
        ];
        f.Rows[SystemFixture.RowKey(RowKinds.Events, StabilityCollector.AppCrashQuery)] =
        [
            Event(1000, "Application Error", Now.AddDays(-1), data0: "OUTLOOK.EXE", data3: "ntdll.dll"),
            Event(1000, "Application Error", Now.AddDays(-2), data0: "OUTLOOK.EXE", data3: "ntdll.dll"),
            Event(1000, "Application Error", Now.AddDays(-3), data0: "outlook.exe", data3: "mso.dll"),
            Event(1000, "Application Error", Now.AddDays(-4), data0: "OUTLOOK.EXE", data3: "ntdll.dll"),
            Event(1000, "Application Error", Now.AddDays(-2), data0: "ms-teams.exe", data3: "KERNELBASE.dll"),
        ];
        f.Rows[SystemFixture.RowKey(RowKinds.Events, StabilityCollector.MemoryTestQuery)] =
        [
            Event(1202, "Microsoft-Windows-MemoryDiagnostics-Results", Now.AddDays(-60)),
        ];
        f.Wmi[SystemFixture.WmiKey(WmiNamespaces.Cimv2, "Win32_ReliabilityStabilityMetrics")] =
        [
            new() { ["SystemStabilityIndex"] = 9.5, ["TimeGenerated"] = "20261115080000.000000+000" },
            new() { ["SystemStabilityIndex"] = 4.0, ["TimeGenerated"] = "20261116080000.000000+000" },
            new() { ["SystemStabilityIndex"] = 3.5, ["TimeGenerated"] = "20261116200000.000000+000" },
            new() { ["SystemStabilityIndex"] = 5.0, ["TimeGenerated"] = "20261118080000.000000+000" },
        ];
        return f;
    }

    [Fact]
    public async Task Stability_CountsGroupsAndFilters()
    {
        var result = await Run(new StabilityCollector(), StabilityFixture());
        var s = result.Get<StabilityInfo>()!;

        Assert.Equal(2, s.BlueScreens.Count); // the 45-day-old one is outside the window
        Assert.Contains(s.BlueScreens, b => b.Detail == "Blue screen 0x9F: DRIVER_POWER_STATE_FAILURE");
        Assert.Contains(s.BlueScreens, b => b.Detail == "Blue screen 0x124: WHEA_UNCORRECTABLE_ERROR");
        // Kernel-Power 41s are counted; the matching EventLog 6008 is not counted twice.
        Assert.Equal(2, s.UnexpectedShutdowns.Count);
        Assert.Contains(s.UnexpectedShutdowns, e => e.Detail.StartsWith("Shut down without warning", StringComparison.Ordinal));
        Assert.Contains(s.UnexpectedShutdowns, e => e.Detail == "Restarted after a blue screen (0x9F)");

        Assert.Equal(5, s.DiskErrors.Count);
        Assert.Equal([1, 3, 1], s.DiskErrorBursts.Select(b => b.Count)); // newest first: bad block, retry burst, single retry
        Assert.Equal(2, s.HardwareErrors.Count);

        var outlook = s.AppCrashes[0];
        Assert.Equal(("OUTLOOK.EXE", 4, "ntdll.dll"), (outlook.App, outlook.Count, outlook.Module));
        Assert.Equal(new MemoryTestResult(Now.AddDays(-60), true), s.LastMemoryTest);

        // One value per day (the last of the day), oldest first.
        Assert.Equal([9.5, 3.5, 5.0], s.Reliability.Select(r => r.Index));
        Assert.Null(s.Minidumps); // needs admin
        Assert.True(result.NeedsAdmin);
    }

    [Fact]
    public async Task Stability_Findings()
    {
        var s = (await Run(new StabilityCollector(), StabilityFixture())).Get<StabilityInfo>()!;
        var findings = HealthRules.Stability(s).ToList();

        Assert.Contains(findings, f => f is { Severity: Severity.Warning } && f.Message.StartsWith("2 blue screens", StringComparison.Ordinal));
        Assert.Contains(findings, f => f is { Severity: Severity.Warning } && f.Message.StartsWith("2 unexpected shutdowns", StringComparison.Ordinal));
        Assert.Contains(findings, f => f is { Severity: Severity.Warning } && f.Message.StartsWith("1 disk read errors", StringComparison.Ordinal));
        Assert.Contains(findings, f => f is { Severity: Severity.Warning } && f.Message.StartsWith("1 hardware errors (WHEA)", StringComparison.Ordinal));
        Assert.Contains(findings, f => f is { Severity: Severity.Info } && f.Message == "OUTLOOK.EXE crashed 4 times in the last 30 days");
        Assert.DoesNotContain(findings, f => f.Message.Contains("ms-teams", StringComparison.Ordinal)); // once is not repeated
        Assert.Contains(findings, f => f is { Severity: Severity.Critical, Area: HealthRules.MemoryArea });
    }

    [Fact]
    public void DiskRetriesOnly_InfoUnlessFrequent()
    {
        StabilityEvent Retry(int day) => new(Now.AddDays(-day), "Disk I/O retried", 153);
        var few = HealthRules.Stability(new StabilityInfo { DiskErrors = [Retry(1), Retry(9)] }).Single();
        Assert.Equal(Severity.Info, few.Severity);
        var many = HealthRules.Stability(new StabilityInfo { DiskErrors = [Retry(1), Retry(5), Retry(9)] }).Single();
        Assert.Equal(Severity.Warning, many.Severity);
    }

    private static SystemFixture UpdatesFixture(string startMode = "Manual")
    {
        var f = new SystemFixture();
        Dictionary<string, object?> U(string title, int daysAgo, int result = 2, int operation = 1) => new()
        {
            ["Title"] = title, ["Date"] = Now.AddDays(-daysAgo).ToString("o"), ["Result"] = (long)result, ["Operation"] = (long)operation,
        };
        f.Rows[RowKinds.UpdateHistory] =
        [
            U("9NRZT3Q9R3DL-Microsoft.WindowsAppRuntime.2", 0),
            U("Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version 1.421.1)", 1),
            U("2026-09 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5065000)", 59),
            U("2026-09 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5065000)", 60, result: 4),
            U("Intel - Display - 31.0.101.5000", 30, result: 4),
        ];
        f.Wmi[SystemFixture.WmiKey(WmiNamespaces.Cimv2, "Win32_Service", "Name = 'wuauserv'")] =
            [new() { ["Name"] = "wuauserv", ["StartMode"] = startMode, ["State"] = "Stopped" }];
        return f;
    }

    [Fact]
    public async Task Updates_HistoryFailuresAndRules()
    {
        var u = (await Run(new UpdatesCollector(), UpdatesFixture())).Get<UpdatesInfo>()!;

        Assert.DoesNotContain(u.History, h => h.Title.StartsWith("9N", StringComparison.Ordinal)); // Store app update
        Assert.StartsWith("Security Intelligence", u.LastInstalled!.Title, StringComparison.Ordinal);
        Assert.Contains("KB5065000", u.LastQualityUpdate!.Title, StringComparison.Ordinal);
        // The cumulative update failed, then succeeded the next day: only the driver counts as failed.
        Assert.Equal(["Intel - Display - 31.0.101.5000"], u.RecentFailures.Select(f => f.Title));

        var findings = HealthRules.Updates(u, null, Now).ToList();
        Assert.Contains(findings, f => f is { Severity: Severity.Warning } && f.Message == "Last monthly update installed 59 days ago");
        Assert.Contains(findings, f => f is { Severity: Severity.Info } && f.Message == "Update failed: Intel - Display - 31.0.101.5000");
        Assert.DoesNotContain(findings, f => f.Message.Contains("disabled", StringComparison.Ordinal));

        var disabled = (await Run(new UpdatesCollector(), UpdatesFixture("Disabled"))).Get<UpdatesInfo>()!;
        Assert.Contains(HealthRules.Updates(disabled, null, Now), f => f.Message == "The Windows Update service is disabled");
    }

    [Fact]
    public void PendingRestart_WarningOnlyAfterAWeek()
    {
        var os = new OsInfo { PendingRebootReasons = ["Windows Update"], Uptime = TimeSpan.FromDays(2) };
        Assert.Equal(Severity.Info, HealthRules.Os(os).Single(f => f.Area == HealthRules.UpdatesArea).Severity);
        Assert.Equal(Severity.Warning, HealthRules.Os(os with { Uptime = TimeSpan.FromDays(9) }).Single(f => f.Area == HealthRules.UpdatesArea).Severity);
    }

    [Fact]
    public void Storage_SmartRules()
    {
        var storage = new StorageInfo
        {
            Disks =
            [
                new PhysicalDiskInfo { Number = 0, Model = "Old SSD", MediaType = "SSD", Health = DiskHealth.Healthy, WearPercent = 85, PredictedFailure = true, ReadErrors = 3, WriteErrors = 0 },
            ],
        };
        var findings = HealthRules.Storage(storage).ToList();
        Assert.Contains(findings, f => f is { Severity: Severity.Critical } && f.Message.StartsWith("SMART predicts a failure", StringComparison.Ordinal));
        Assert.Contains(findings, f => f is { Severity: Severity.Warning } && f.Message == "Disk 0 (Old SSD) is 85% worn");
        Assert.Contains(findings, f => f.Message.Contains("uncorrected read/write errors (3 read, 0 write)", StringComparison.Ordinal));
    }
}

public class LifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(19045, "Professional", SupportState.OutOfSupport)]
    [InlineData(19045, "Enterprise", SupportState.OutOfSupport)]
    [InlineData(26100, "Professional", SupportState.OutOfSupport)]  // 24H2 Home/Pro ended 13 Oct 2026
    [InlineData(26100, "Enterprise", SupportState.Supported)]      // until 12 Oct 2027
    [InlineData(22631, "Education", SupportState.OutOfSupport)]    // 23H2 Enterprise/Education ended 10 Nov 2026
    [InlineData(26200, "Core", SupportState.Supported)]            // 25H2
    [InlineData(26300, "Professional", SupportState.NewerThanTable)]
    [InlineData(19044, "EnterpriseS", SupportState.EndingSoon)]    // LTSC 2021: 12 Jan 2027
    [InlineData(17763, "EnterpriseS", SupportState.Supported)]     // LTSC 2019: 2029
    [InlineData(17134, "Professional", SupportState.OutOfSupport)] // older than the table
    public void BuildAndEdition(int build, string edition, SupportState expected) =>
        Assert.Equal(expected, Lifecycle.BuiltIn().Check(new OsInfo { Build = build, EditionId = edition }, Now).State);

    [Fact]
    public void OutOfSupport_IsCritical_WithTheEsuNote()
    {
        var finding = HealthRules.Support(new OsInfo { Name = "Windows 10 Pro", DisplayVersion = "22H2", Build = 19045, EditionId = "Professional" }, Now).Single();
        Assert.Equal(Severity.Critical, finding.Severity);
        Assert.Contains("Extended Security Updates", finding.Recommendation, StringComparison.Ordinal);
        Assert.StartsWith("Upgrade to Windows 11", finding.Recommendation, StringComparison.Ordinal);
    }

    [Fact]
    public void OverrideFile_NextToTheExe()
    {
        var folder = Directory.CreateTempSubdirectory("unravel-lifecycle").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, Lifecycle.FileName),
                """{ "releases": [ { "product": "Windows 11", "version": "26H2", "build": 26300, "homePro": "2028-10-10", "enterprise": "2029-10-09" } ] }""");
            var table = Lifecycle.Load(folder);
            Assert.EndsWith(Lifecycle.FileName, table.Source, StringComparison.Ordinal);
            var status = table.Check(new OsInfo { Build = 26300, EditionId = "Professional" }, Now);
            Assert.Equal((SupportState.Supported, new DateOnly(2028, 10, 10)), (status.State, status.EndDate));

            File.WriteAllText(Path.Combine(folder, Lifecycle.FileName), "not json");
            Assert.Equal("built in", Lifecycle.Load(folder).Source); // a broken file falls back
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}

public class Win11Tests
{
    private static ProcessorInfo Cpu(string name, string? arch = "x64")
    {
        var clean = CpuRules.CleanName(name);
        var (gen, number) = CpuRules.Generation(clean);
        return new ProcessorInfo { Name = clean, Manufacturer = CpuRules.Vendor(null, clean), Architecture = arch, Generation = gen, GenerationNumber = number };
    }

    [Theory]
    [InlineData("Intel(R) Core(TM) i5-8250U CPU @ 1.60GHz", CheckResult.Pass)]
    [InlineData("Intel(R) Core(TM) i7-7700HQ CPU @ 2.80GHz", CheckResult.Fail)]
    [InlineData("Intel(R) Core(TM) i7-7820HQ CPU @ 2.90GHz", CheckResult.Pass)]
    [InlineData("13th Gen Intel(R) Core(TM) i5-1335U", CheckResult.Pass)]
    [InlineData("Intel(R) Core(TM) Ultra 7 155H", CheckResult.Pass)]
    [InlineData("Intel(R) Celeron(R) N4020 CPU @ 1.10GHz", CheckResult.Pass)]
    [InlineData("Intel(R) Celeron(R) N3350 CPU @ 1.10GHz", CheckResult.Fail)]
    [InlineData("Intel(R) N100", CheckResult.Pass)]
    [InlineData("Intel(R) Pentium(R) Gold 7505 @ 2.00GHz", CheckResult.Pass)]
    [InlineData("Intel(R) Xeon(R) E-2276G CPU @ 3.80GHz", CheckResult.Pass)]
    [InlineData("Intel(R) Xeon(R) CPU E3-1240 v5 @ 3.50GHz", CheckResult.Unknown)]
    [InlineData("AMD Ryzen 5 2600 Six-Core Processor", CheckResult.Pass)]
    [InlineData("AMD Ryzen 5 2500U with Radeon Vega Mobile Gfx", CheckResult.Fail)]
    [InlineData("AMD Ryzen 5 2400G with Radeon Vega Graphics", CheckResult.Fail)]
    [InlineData("AMD Ryzen 7 1700X Eight-Core Processor", CheckResult.Fail)]
    [InlineData("AMD Ryzen 5 3500U with Radeon Vega Mobile Gfx", CheckResult.Pass)]
    [InlineData("AMD Athlon Silver 3050U with Radeon Graphics", CheckResult.Pass)]
    [InlineData("AMD Athlon 200GE with Radeon Vega Graphics", CheckResult.Fail)]
    [InlineData("AMD A9-9425 RADEON R5, 5 COMPUTE CORES 2C+3G", CheckResult.Fail)]
    [InlineData("Snapdragon(R) X Elite - X1E78100 - Qualcomm(R) Oryon(TM) CPU", CheckResult.Pass)]
    public void Processor(string name, CheckResult expected) => Assert.Equal(expected, Win11Check.CpuSupported(Cpu(name)).Result);

    [Fact]
    public void ThirtyTwoBitFails() => Assert.Equal(CheckResult.Fail, Win11Check.CpuSupported(Cpu("Intel(R) Core(TM) i5-8250U", "x86")).Result);

    [Fact]
    public async Task WhiteBoxDesktop_NotReadyBecauseOfMbr()
    {
        var r = Win11Check.Evaluate(await FixtureScanTests.Scan("whitebox-desktop-problems.json"));
        Assert.Equal(ReadinessState.NotReady, r.State);
        Assert.False(r.AlreadyWindows11);
        Assert.Equal(["GPT system disk"], r.Blockers.Select(b => b.Requirement));
        Assert.Equal(CheckResult.Pass, r.Checks.Single(c => c.Requirement == "Processor").Result); // i5-8400
        Assert.Contains(HealthRules.Windows11(await FixtureScanTests.Scan("whitebox-desktop-problems.json")), f => f.Severity == Severity.Warning);
    }

    [Fact]
    public void SecureBootOff_IsStillCapable()
    {
        var scan = new ScanResult
        {
            Results =
            [
                new CollectorResult { Section = Sections.Firmware, Data = new FirmwareInfo { BootMode = "UEFI", SecureBoot = false, Tpm = new TpmInfo { Present = true, Version = "2.0" } } },
            ],
        };
        var checks = Win11Check.Evaluate(scan).Checks.ToDictionary(c => c.Requirement);
        Assert.Equal(CheckResult.Pass, checks["Secure Boot capable"].Result);
        Assert.Equal(CheckResult.Pass, checks["TPM 2.0"].Result);
        Assert.Equal(CheckResult.Unknown, checks["Processor"].Result);
    }

    [Fact]
    public async Task SummaryBadge_AndDiagnosticsPage()
    {
        var scan = await FixtureScanTests.Scan("whitebox-desktop-problems.json");
        Assert.Equal((Severity?)Severity.Warning, SummaryBuilder.Build(scan).Health.Single(h => h.Area == "Windows 11").Severity);

        var cards = DiagnosticsPage.Build(scan, []);
        Assert.Equal(["Health checks", "Windows 11 readiness", "Support and updates", "Stability"], cards.Select(c => c.Title));
        Assert.Contains(cards[0].Lines, l => l.Severity == Severity.Critical);
    }
}

public class ActiveTestLogicTests
{
    [Theory]
    [InlineData(3400, 1600, "NVMe SSD", Severity.Ok)]
    [InlineData(500, 450, "NVMe SSD", Severity.Warning)]
    [InlineData(520, 480, "SATA SSD", Severity.Ok)]
    [InlineData(150, 140, "SATA HDD", Severity.Ok)]
    [InlineData(40, 35, "SATA HDD", Severity.Warning)]
    [InlineData(40, 35, null, Severity.Ok)]
    public void DiskSpeed(double read, double write, string? kind, Severity expected) =>
        Assert.Equal(expected, DiskSpeedTest.Classify(read, write, kind).Severity);

    [Fact]
    public void Network()
    {
        PingStats P(string label, int received, double? avg) => new("x", label, 10, received, avg, avg);

        Assert.Equal(Severity.Ok, NetworkTest.Evaluate([P("Gateway", 10, 2), P("Internet (1.1.1.1)", 10, 15), P("Internet (8.8.8.8)", 10, 18)], 20, null, null).Severity);
        var lossy = NetworkTest.Evaluate([P("Gateway", 10, 2), P("Internet (1.1.1.1)", 8, 15), P("Internet (8.8.8.8)", 10, 18)], 20, 95.4, null);
        Assert.Equal(Severity.Warning, lossy.Severity);
        Assert.Contains("95 Mbps", lossy.Summary, StringComparison.Ordinal);
        var offline = NetworkTest.Evaluate([P("Gateway", 10, 2), P("Internet (1.1.1.1)", 0, null), P("Internet (8.8.8.8)", 0, null)], null, null, null);
        Assert.Equal((Severity.Critical, "No internet: the gateway answers, but nothing beyond it"), (offline.Severity, offline.Summary));
    }

    [Fact]
    public void SystemFileCheckOutput()
    {
        string[] dismOk = ["No component store corruption detected.", "The operation completed successfully."];
        Assert.Equal(Severity.Ok, SystemFileCheck.Evaluate(0, ["Windows Resource Protection did not find any integrity violations."], 0, dismOk).Severity);
        Assert.Equal(Severity.Warning, SystemFileCheck.Evaluate(0, ["Windows Resource Protection found corrupt files and successfully repaired them."], 0, dismOk).Severity);
        Assert.Equal(Severity.Critical, SystemFileCheck.Evaluate(1, ["Windows Resource Protection found corrupt files but was unable to fix some of them."], 0, dismOk).Severity);
        Assert.Equal(Severity.Critical, SystemFileCheck.Evaluate(0, ["did not find any integrity violations"], 0, ["The component store is not repairable."]).Severity);
        Assert.Equal(Severity.Info, SystemFileCheck.Evaluate(0, ["Ressourcenschutz hat keine Integritätsverletzungen gefunden."], 0, dismOk).Severity);
    }

    [Theory]
    [InlineData("9NRZT3Q9R3DL-Microsoft.WindowsAppRuntime.2", true)]
    [InlineData("2026-11 Security Update (KB5132061) (26300.9974)", false)]
    public void StorePackageUpdates(string title, bool expected) => Assert.Equal(expected, UpdateRules.IsStorePackage(title));

    [Fact]
    public void BugCheckNames()
    {
        Assert.Equal("Blue screen 0xD1: DRIVER_IRQL_NOT_LESS_OR_EQUAL", StabilityRules.BugCheck("0x000000d1 (0x1, 0x2)"));
        Assert.Equal("Blue screen 0xBEEF", StabilityRules.BugCheck("0x0000beef (0)"));
        Assert.Equal("Shut down without warning (power loss, forced power-off or a hang)", StabilityRules.KernelPower41("0"));
    }
}

public class CpuStressTests
{
    private static List<CpuSample> Run(Func<int, double?> clock, Func<int, double?>? temp = null) =>
        Enumerable.Range(1, 30).Select(s => new CpuSample(s, clock(s), temp?.Invoke(s), 100)).ToList();

    [Fact]
    public void Stable()
    {
        var o = CpuStressTest.Evaluate(Run(_ => 3000), 30, 12, "needs administrator rights");
        Assert.Equal((Severity.Ok, "Stable at 3.00 GHz under full load"), (o.Severity, o.Summary.Replace(',', '.')));
        Assert.Contains(o.Details, d => d == "Temperature: not available (needs administrator rights).");
        Assert.Contains(o.Details, d => d == "Average processor load: 100%.");
    }

    [Fact]
    public void TurboSettling_IsNormal()
    {
        var o = CpuStressTest.Evaluate(Run(s => s <= 5 ? 4200 : 3600), 30, 8, null);
        Assert.Equal(Severity.Ok, o.Severity);
        Assert.Contains("normal for this kind of PC", o.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Throttling_IsAWarning()
    {
        var o = CpuStressTest.Evaluate(Run(s => s <= 7 ? 3800 : 1900), 30, 8, null);
        Assert.Equal(Severity.Warning, o.Severity);
        Assert.StartsWith("Throttling: the clock fell 50% under load", o.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Hot_IsAWarningEvenWithoutThrottling()
    {
        var o = CpuStressTest.Evaluate(Run(_ => 3000, s => 60 + s * 1.2), 30, 8, null);
        Assert.Equal(Severity.Warning, o.Severity);
        Assert.Contains(o.Details, d => d.StartsWith("It reached 96 °C", StringComparison.Ordinal));
    }

    [Fact]
    public void NoClock_CompletesAsInfo()
    {
        var o = CpuStressTest.Evaluate(Run(_ => null), 30, 4, null);
        Assert.Equal((Severity.Info, "Completed (clock speed not available)"), (o.Severity, o.Summary));
    }
}

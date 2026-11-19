using Unravel.Core.Collectors;
using Unravel.Core.Diagnostics;
using Unravel.Core.Infrastructure;
using Unravel.Core.Models;
using Unravel.Core.Summary;

namespace Unravel.Core.Tests;

/// <summary>Full scans replayed from captured (or synthetic) machines in Fixtures/.</summary>
public class FixtureScanTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    internal static async Task<ScanResult> Scan(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        var ctx = new CollectContext(new FixtureSystemSource(SystemFixture.Load(path)), IsAdmin: false, Now);
        return await ScanRunner.CreateDefault().RunAsync(ctx);
    }

    [Fact]
    public async Task AsusLaptop_ReadsEverythingPhase1Needs()
    {
        var scan = await Scan("asus-expertbook-b1403cva.json");

        Assert.All(scan.Results, r => Assert.False(r.Failed, r.Section));
        Assert.Empty(scan.Warnings);

        var device = scan.Device!;
        Assert.Equal("ASUS", device.Make);
        Assert.Equal("ASUS EXPERTBOOK B1403CVA", device.DisplayName);
        Assert.Equal("TESTSERIAL01", device.SerialNumber);
        Assert.Null(device.Sku); // " " in WMI
        Assert.Equal(ChassisKind.Laptop, device.Chassis);
        Assert.False(device.IsVirtualMachine);
        Assert.Equal("WORKGROUP", device.Workgroup);

        var cpu = scan.Cpu!.Primary!;
        Assert.Equal("13th Gen Intel Core i5-1335U", cpu.Name);
        Assert.Equal(13, cpu.GenerationNumber);
        Assert.Equal(10, cpu.Cores);
        Assert.Equal(12, cpu.LogicalProcessors);
        Assert.Equal("x64", cpu.Architecture);

        var memory = scan.Memory!;
        Assert.Equal("DDR5", memory.Generation);
        Assert.False(memory.GenerationInferred);
        Assert.Equal(16UL * 1024 * 1024 * 1024, memory.InstalledBytes);
        Assert.Equal(2, memory.TotalSlots);
        Assert.Equal(1, memory.SlotsFree);
        Assert.Equal("SODIMM", memory.Modules[0].FormFactor);
        Assert.Equal(5600, memory.Modules[0].RatedSpeed);
        Assert.Equal(5200, memory.Modules[0].RunningSpeed);
        Assert.Equal("M425R2GA3EB0-CWMOL", memory.Modules[0].PartNumber);

        var disk = Assert.Single(scan.Storage!.Disks);
        Assert.Equal("NVMe SSD", disk.Kind);
        Assert.Equal("GPT", disk.PartitionStyle);
        Assert.True(disk.IsSystemDisk);
        Assert.Equal(DiskHealth.Healthy, disk.Health);
        var c = Assert.Single(scan.Storage.Volumes);
        Assert.True(c.IsSystemDrive);
        Assert.InRange(c.PercentUsed, 46, 47);

        var os = scan.Os!;
        Assert.Equal("Windows 11 Pro", os.Name);
        Assert.Equal("26H2", os.DisplayVersion);
        Assert.Equal("26300.9974", os.FullBuild);
        Assert.Equal("x64", os.Architecture);
        Assert.True(os.IsActivated);
        Assert.Equal("OEM", os.LicenseChannel);
        Assert.False(os.PendingReboot);
        Assert.Equal("en-US", os.Locale);
    }

    [Fact]
    public async Task WhiteBoxDesktop_TreatsOemPlaceholdersAsNotSet()
    {
        var scan = await Scan("whitebox-desktop-problems.json");
        var device = scan.Device!;

        Assert.Null(device.Manufacturer);
        Assert.Null(device.Model);
        Assert.Null(device.SerialNumber);
        Assert.Null(device.Sku);
        Assert.Null(device.Uuid);
        Assert.Equal("Unknown device", device.DisplayName);
        Assert.Equal(ChassisKind.Desktop, device.Chassis);
        Assert.True(device.PartOfDomain);
        Assert.Equal("contoso.local", device.Domain);

        var identity = SummaryBuilder.Build(scan).Identity;
        Assert.Equal("Not set", identity.Single(f => f.Label == "Serial number").Value);
        Assert.False(identity.Single(f => f.Label == "Serial number").Copyable);
    }

    [Fact]
    public async Task WhiteBoxDesktop_MemoryInferredAndFlagged()
    {
        var memory = (await Scan("whitebox-desktop-problems.json")).Memory!;

        Assert.Equal(2, memory.Modules.Count);
        Assert.Equal("DDR4", memory.Modules[0].Generation);
        Assert.False(memory.Modules[0].GenerationInferred);
        Assert.Equal("DDR4", memory.Modules[1].Generation); // from the Samsung part number
        Assert.True(memory.Modules[1].GenerationInferred);
        Assert.Equal("Samsung", memory.Modules[1].Manufacturer); // JEDEC 80CE
        Assert.Equal("DIMM", memory.Modules[1].FormFactor);
        Assert.Equal(12UL * 1024 * 1024 * 1024, memory.InstalledBytes);
        Assert.Equal(2, memory.SlotsFree);
        Assert.True(memory.Mismatched);
        Assert.True(memory.RunningBelowRated);
        Assert.False(memory.SingleChannel);
    }

    [Fact]
    public async Task WhiteBoxDesktop_StorageAndOs()
    {
        var scan = await Scan("whitebox-desktop-problems.json");
        var storage = scan.Storage!;

        Assert.Equal("SATA HDD", storage.SystemDisk!.Kind);
        Assert.Equal("MBR", storage.SystemDisk.PartitionStyle);
        Assert.True(storage.Disks[1].IsUsb);
        Assert.Null(storage.Disks[1].SerialNumber);
        // Network (Z:) and optical (D:) drives are left out; the USB stick is kept as removable.
        Assert.Equal(["C:", "E:"], storage.Volumes.Select(v => v.Letter));
        Assert.True(storage.Volumes[1].IsRemovable);

        var os = scan.Os!;
        Assert.Equal("Windows 10 Pro", os.Name);
        Assert.Equal("19045.5131", os.FullBuild);
        Assert.False(os.IsActivated);
        Assert.Equal("Retail", os.LicenseChannel);
        Assert.Equal(["Windows Update"], os.PendingRebootReasons);
        Assert.True(os.PendingFileRenames);
        Assert.Equal("en-GB", os.Locale);
        Assert.Null(os.RegisteredOwner);
        Assert.Equal(30, (int)os.Uptime!.Value.TotalDays);
    }

    [Fact]
    public async Task WhiteBoxDesktop_Findings()
    {
        var findings = HealthRules.Evaluate(await Scan("whitebox-desktop-problems.json"));

        Assert.Contains(findings, f => f.Severity == Severity.Critical && f.Message.StartsWith("Drive C: is 97% full", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Severity == Severity.Info && f.Message.Contains("hard disk", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Severity == Severity.Warning && f.Message.Contains("not activated", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Severity == Severity.Warning && f.Area == HealthRules.UpdatesArea);
        Assert.Contains(findings, f => f.Message.Contains("different sizes", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Message.StartsWith("Not restarted for 30 days", StringComparison.Ordinal));
        // 12 GB is enough: no low-memory warning.
        Assert.DoesNotContain(findings, f => f.Message.Contains("of memory installed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HyperVVm_PartialDataBecomesWarningsNotFailures()
    {
        var scan = await Scan("hyperv-vm-limited.json");

        Assert.All(scan.Results, r => Assert.False(r.Failed, r.Section));
        Assert.True(scan.NeedsAdmin); // MSFT_PhysicalDisk was denied

        var device = scan.Device!;
        Assert.True(device.IsVirtualMachine);
        Assert.Equal("Hyper-V", device.VmPlatform);
        Assert.Equal(ChassisKind.VirtualMachine, device.Chassis);
        Assert.Null(device.Sku);

        var cpu = scan.Cpu!.Primary!;
        Assert.Equal("AMD Ryzen 7 5800X 8-Core", cpu.Name);
        Assert.Equal("Ryzen 5000 series", cpu.Generation);
        Assert.Null(cpu.VirtualizationEnabled);
        Assert.Null(cpu.Socket);

        var memory = scan.Memory!;
        Assert.Empty(memory.Modules);
        Assert.Equal(4UL * 1024 * 1024 * 1024, memory.InstalledBytes);
        Assert.Null(memory.TotalSlots);

        var disk = Assert.Single(scan.Storage!.Disks);
        Assert.Equal("Microsoft Virtual Disk", disk.Model);
        Assert.Equal(DiskHealth.Healthy, disk.Health);

        Assert.Equal("Windows 11 Enterprise", scan.Os!.Name);
        Assert.Null(scan.Os.IsActivated);

        Assert.Contains(scan.Warnings, w => w == "Couldn't read physical disks (needs administrator)");
        Assert.Contains(scan.Warnings, w => w.StartsWith("Couldn't read memory slots", StringComparison.Ordinal));
        Assert.Contains(scan.Warnings, w => w.StartsWith("Couldn't read activation status", StringComparison.Ordinal));
        Assert.Contains(HealthRules.Evaluate(scan), f => f.Severity == Severity.Warning && f.Message == "Only 4 GB of memory installed");
    }

    [Fact]
    public async Task Summary_BuildsForEveryFixture()
    {
        foreach (var name in new[] { "asus-expertbook-b1403cva.json", "whitebox-desktop-problems.json", "hyperv-vm-limited.json" })
        {
            var scan = await Scan(name);
            var summary = SummaryBuilder.Build(scan);
            Assert.Equal(5, summary.Specs.Count);
            Assert.All(summary.Specs, s => Assert.Equal(TileState.Ready, s.State));
            Assert.Equal(6, summary.Health.Count);
            Assert.False(string.IsNullOrWhiteSpace(SummaryBuilder.PlainText(scan)));
        }
    }
}

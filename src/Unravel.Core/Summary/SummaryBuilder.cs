using System.Text;
using Unravel.Core.Diagnostics;
using Unravel.Core.Infrastructure;
using Unravel.Core.Models;

namespace Unravel.Core.Summary;

public enum TileState
{
    Loading,
    Ready,
    Failed,
}

/// <summary>A labelled value ("Serial number", "PF3ABC12").</summary>
public sealed record Field(string Label, string Value, bool Copyable = false);

/// <summary>One key spec in the row under the header (CPU · RAM · Storage · OS · Uptime).</summary>
public sealed record SpecTile(string Label, string Value, string? Detail, TileState State, string? ToolTip = null);

/// <summary>A traffic-light badge in the health strip. Null severity means "not checked yet".</summary>
public sealed record HealthBadge(string Area, Severity? Severity, string Text);

public sealed record ModuleRow(string Slot, string Size, string Generation, string Speed, string Manufacturer, string PartNumber, string FormFactor);

public sealed record VolumeRow(string Letter, string Label, string Detail, double PercentUsed, Severity Severity);

public sealed record DiskRow(string Title, string Detail, Severity Health, string HealthText, string? SerialNumber);

/// <summary>Everything the Summary page shows, built from a (possibly still running) scan.</summary>
public sealed record SummaryModel
{
    public required string Title { get; init; }

    public string? Subtitle { get; init; }

    public IReadOnlyList<Field> Identity { get; init; } = [];

    public IReadOnlyList<SpecTile> Specs { get; init; } = [];

    public IReadOnlyList<HealthBadge> Health { get; init; } = [];

    public IReadOnlyList<Finding> Findings { get; init; } = [];

    public IReadOnlyList<string> DataNotes { get; init; } = [];

    public IReadOnlyList<Field> Processor { get; init; } = [];

    public IReadOnlyList<ModuleRow> Modules { get; init; } = [];

    public bool HasModules => Modules.Count > 0;

    public string? MemorySummary { get; init; }

    public string? MemoryUpgrade { get; init; }

    public IReadOnlyList<DiskRow> Disks { get; init; } = [];

    public IReadOnlyList<VolumeRow> Volumes { get; init; } = [];

    public IReadOnlyList<Field> Windows { get; init; } = [];
}

/// <summary>Builds the <see cref="SummaryModel"/> and the plain-text "Copy summary" (plan.md section 4.1).</summary>
public static class SummaryBuilder
{
    /// <param name="scan">Results so far.</param>
    /// <param name="pending">Sections still running (shown as "Reading...").</param>
    public static SummaryModel Build(ScanResult scan, IReadOnlyCollection<string>? pending = null)
    {
        pending ??= [];
        bool IsPending(string s) => pending.Contains(s, StringComparer.OrdinalIgnoreCase);
        TileState State(string s, object? data) => data is not null ? TileState.Ready : IsPending(s) ? TileState.Loading : TileState.Failed;

        var device = scan.Device;
        var findings = HealthRules.Evaluate(scan).OrderByDescending(f => f.Severity).ToList();

        return new SummaryModel
        {
            Title = device?.DisplayName ?? (IsPending(Sections.Device) ? "Reading this PC..." : "Unknown device"),
            Subtitle = device is null ? null : Join(" · ", device.ChassisText == "Unknown" ? null : device.ChassisText, device.ComputerName,
                device.PartOfDomain ? $"Domain {device.Domain}" : device.Workgroup is { } wg ? $"Workgroup {wg}" : null),
            Identity = device is null ? [] : IdentityFields(device),
            Specs =
            [
                CpuTile(scan.Cpu, State(Sections.Cpu, scan.Cpu)),
                MemoryTile(scan.Memory, State(Sections.Memory, scan.Memory)),
                StorageTile(scan.Storage, State(Sections.Storage, scan.Storage)),
                OsTile(scan.Os, State(Sections.Os, scan.Os)),
                UptimeTile(scan.Os, State(Sections.Os, scan.Os)),
            ],
            Health = HealthStrip(scan, findings, IsPending),
            Findings = findings.Where(f => f.Severity != Severity.Ok).ToList(),
            DataNotes = scan.Warnings.Distinct().ToList(),
            Processor = scan.Cpu is null ? [] : ProcessorFields(scan.Cpu),
            Modules = scan.Memory?.Modules.Select(ModuleRow).ToList() ?? [],
            MemorySummary = scan.Memory is null ? null : MemoryText(scan.Memory),
            MemoryUpgrade = scan.Memory is null ? null : HealthRules.MemoryUpgradeText(scan.Memory),
            Disks = scan.Storage?.Disks.Select(DiskRow).ToList() ?? [],
            Volumes = scan.Storage?.Volumes.Select(VolumeRow).ToList() ?? [],
            Windows = scan.Os is null ? [] : WindowsFields(scan.Os),
        };
    }

    private static List<Field> IdentityFields(DeviceInfo d)
    {
        var fields = new List<Field>
        {
            new("Serial number", OemValues.OrNotSet(d.SerialNumber), d.SerialNumber is not null),
            new("Manufacturer", OemValues.OrNotSet(d.Manufacturer)),
            new("Model", d.FriendlyModel is null ? OemValues.OrNotSet(d.Model) : $"{d.FriendlyModel} ({d.Model})"),
            new("SKU / product number", OemValues.OrNotSet(d.Sku), d.Sku is not null),
            new("UUID", OemValues.OrNotSet(d.Uuid), d.Uuid is not null),
        };
        return fields;
    }

    private static SpecTile CpuTile(CpuInfo? cpu, TileState state)
    {
        if (cpu?.Primary is not { } p)
        {
            return new("Processor", Placeholder(state), null, state == TileState.Ready ? TileState.Failed : state);
        }

        var detail = Join(" · ",
            cpu.TotalCores is { } c ? $"{c} cores / {cpu.TotalThreads} threads" : null,
            p.BaseClockMhz is { } mhz ? Formatting.Clock(mhz) : null);
        var name = cpu.Sockets > 1 ? $"{cpu.Sockets} × {p.Name}" : p.Name ?? "Unknown processor";
        return new("Processor", name, detail, state, p.Generation);
    }

    private static SpecTile MemoryTile(MemoryInfo? memory, TileState state)
    {
        if (memory is null)
        {
            return new("Memory", Placeholder(state), null, state);
        }

        var gen = memory.Generation is { } g ? $" {g}" : "";
        var detail = memory.TotalSlots is { } total
            ? $"{memory.SlotsUsed} of {total} slots used"
            : memory.AllSoldered ? "Soldered" : memory.Modules.Count > 0 ? $"{memory.Modules.Count} module(s)" : null;
        var tip = memory.UsableBytes is { } usable ? $"{Formatting.Memory(usable, exact: true)} usable by Windows" : null;
        return new("Memory", $"{Formatting.Memory(memory.InstalledBytes)}{gen}", detail, state, tip);
    }

    private static SpecTile StorageTile(StorageInfo? storage, TileState state)
    {
        if (storage is null)
        {
            return new("Storage", Placeholder(state), null, state);
        }

        var disks = storage.InternalDisks.ToList();
        var value = disks.Count switch
        {
            0 => storage.SystemVolume is { } v ? Formatting.DiskSize(v.SizeBytes) : "No disks found",
            1 => $"{Formatting.DiskSize(disks[0].SizeBytes)} {disks[0].Kind}",
            _ => $"{disks.Count} disks · {Formatting.DiskSize(disks.Aggregate(0UL, (s, d) => s + (d.SizeBytes ?? 0)))}",
        };
        var sys = storage.SystemVolume;
        var detail = sys is null ? null : $"{sys.Letter} {Formatting.Percent(sys.PercentUsed)} used · {Formatting.VolumeSize(sys.FreeBytes)} free";
        return new("Storage", value, detail, state);
    }

    private static SpecTile OsTile(OsInfo? os, TileState state)
    {
        if (os is null)
        {
            return new("Windows", Placeholder(state), null, state);
        }

        return new("Windows", os.Name ?? "Windows", Join(" · ", os.DisplayVersion, os.FullBuild is { } b ? $"build {b}" : null, os.Architecture), state);
    }

    private static SpecTile UptimeTile(OsInfo? os, TileState state)
    {
        if (os is null)
        {
            return new("Uptime", Placeholder(state), null, state);
        }

        return new("Uptime", Formatting.Duration(os.Uptime), os.LastBoot is { } b ? $"since {Formatting.DateTime(b)}" : null, state,
            "Time since the last restart. With Fast Startup on, shutting down does not reset it.");
    }

    private static string Placeholder(TileState state) => state == TileState.Loading ? "Reading..." : "Couldn't read";

    private static List<HealthBadge> HealthStrip(ScanResult scan, IReadOnlyList<Finding> findings, Func<string, bool> isPending)
    {
        HealthBadge Badge(string area, string section, object? data, string okText, string findingArea)
        {
            if (data is null)
            {
                return new(area, null, isPending(section) ? "Checking..." : "Couldn't check");
            }

            var worst = findings.Where(f => f.Area == findingArea).OrderByDescending(f => f.Severity).FirstOrDefault();
            return worst is null || worst.Severity == Severity.Info
                ? new(area, Severity.Ok, okText)
                : new(area, worst.Severity, worst.Severity == Severity.Critical ? "Action needed" : "Attention");
        }

        return
        [
            Badge("Storage", Sections.Storage, scan.Storage, "OK", HealthRules.StorageArea),
            Badge("Memory", Sections.Memory, scan.Memory, "OK", HealthRules.MemoryArea),
            // Phase 1 knows about pending restarts only; update history arrives with the full OS section.
            Badge("Updates", Sections.Updates, scan.Updates, "Up to date", HealthRules.UpdatesArea),
            Badge("Security", Sections.Security, scan.Security, "Protected", HealthRules.SecurityArea),
            Windows11Badge(scan, isPending),
            scan.Battery is { HasBattery: false }
                ? new HealthBadge("Battery", null, "No battery")
                : Badge("Battery", Sections.Battery, scan.Battery,
                    scan.Battery?.Primary?.HealthPercent is { } h ? $"{h:0}% health" : "OK", HealthRules.BatteryArea),
        ];
    }

    private static HealthBadge Windows11Badge(ScanResult scan, Func<string, bool> isPending)
    {
        if (isPending(Sections.Cpu) || isPending(Sections.Firmware) || isPending(Sections.Storage) || isPending(Sections.Memory))
        {
            return new("Windows 11", null, "Checking...");
        }

        var r = Win11Check.Evaluate(scan);
        return (r.State, r.AlreadyWindows11) switch
        {
            (ReadinessState.Ready, true) => new("Windows 11", Severity.Ok, "Supported"),
            (ReadinessState.Ready, false) => new("Windows 11", Severity.Ok, "Ready"),
            (ReadinessState.NotReady, true) => new("Windows 11", Severity.Info, "Unsupported hardware"),
            (ReadinessState.NotReady, false) => new("Windows 11", Severity.Warning, "Not ready"),
            _ => new("Windows 11", null, "Unknown"),
        };
    }

    private static List<Field> ProcessorFields(CpuInfo cpu)
    {
        var p = cpu.Primary!;
        var fields = new List<Field>
        {
            new("Name", cpu.Sockets > 1 ? $"{cpu.Sockets} × {p.Name}" : OemValues.OrNotSet(p.Name)),
            new("Cores / threads", cpu.TotalCores is null ? "?" : $"{cpu.TotalCores} / {cpu.TotalThreads}"),
            new("Base clock", Formatting.Clock(p.BaseClockMhz)),
        };
        if (p.Generation is { } gen)
        {
            fields.Add(new("Generation", gen));
        }

        fields.Add(new("Cache", Join(" · ", p.L2CacheKb is { } l2 ? $"L2 {Formatting.Cache(l2)}" : null, p.L3CacheKb is { } l3 ? $"L3 {Formatting.Cache(l3)}" : null) ?? "?"));
        fields.Add(new("Architecture", p.Architecture ?? "?"));
        fields.Add(new("Socket", OemValues.OrNotSet(p.Socket)));
        fields.Add(new("Virtualisation", p.VirtualizationEnabled switch
        {
            true => "Enabled in firmware",
            false => "Disabled in firmware",
            null => "Not reported (a hypervisor is running)",
        }));
        return fields;
    }

    private static ModuleRow ModuleRow(MemoryModule m) => new(
        OemValues.OrNotSet(m.Slot ?? m.BankLabel),
        Formatting.Memory(m.CapacityBytes),
        m.Generation is null ? "Unknown" : m.GenerationInferred ? $"{m.Generation} (likely)" : m.Generation,
        m.RatedSpeed is null ? "?" : m.RunningSpeed is { } run && run != m.RatedSpeed ? $"{run} of {m.RatedSpeed} MT/s" : $"{m.RatedSpeed} MT/s",
        OemValues.OrNotSet(m.Manufacturer),
        OemValues.OrNotSet(m.PartNumber),
        m.FormFactor ?? "?");

    private static string MemoryText(MemoryInfo m) => Join(" · ",
        $"{Formatting.Memory(m.InstalledBytes)} installed",
        m.UsableBytes is { } u ? $"{Formatting.Memory(u, exact: true)} usable" : null,
        m.Generation is { } g ? m.GenerationInferred ? $"{g} (likely, from part number or speed)" : g : null)!;

    private static DiskRow DiskRow(PhysicalDiskInfo d)
    {
        var title = $"Disk {d.Number?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}: {d.Model ?? "Unknown model"}";
        var detail = Join(" · ", Formatting.DiskSize(d.SizeBytes), d.Kind, d.PartitionStyle, d.IsSystemDisk ? "Windows disk" : null);
        var (severity, text) = d.Health switch
        {
            DiskHealth.Healthy => (Severity.Ok, "Healthy"),
            DiskHealth.Warning => (Severity.Critical, "Warning"),
            DiskHealth.Unhealthy => (Severity.Critical, "Unhealthy"),
            _ => (Severity.Info, "Health unknown"),
        };
        return new(title, detail!, severity, text, d.SerialNumber);
    }

    private static VolumeRow VolumeRow(VolumeInfo v)
    {
        var severity = !v.IsSystemDrive ? (v.PercentFree < 5 ? Severity.Warning : Severity.Ok)
            : v.PercentFree < 5 ? Severity.Critical : v.PercentFree < 15 ? Severity.Warning : Severity.Ok;
        var label = Join(" · ", v.Label, v.FileSystem, v.IsRemovable ? "Removable" : null, v.IsSystemDrive ? "Windows" : null) ?? "";
        var detail = $"{Formatting.VolumeSize(v.FreeBytes)} free of {Formatting.VolumeSize(v.SizeBytes)} ({Formatting.Percent(v.PercentUsed)} used)";
        return new(v.Letter, label, detail, v.PercentUsed, severity);
    }

    private static List<Field> WindowsFields(OsInfo os)
    {
        var fields = new List<Field>
        {
            new("Edition", os.Name ?? os.EditionId ?? "?"),
            new("Version", Join(" · ", os.DisplayVersion, os.FullBuild is { } b ? $"build {b}" : null, os.Architecture) ?? "?"),
            new("Installed", Formatting.Date(os.InstallDate)),
            new("Last restart", Formatting.DateTime(os.LastBoot)),
            new("Activation", os.ActivationStatus is null ? "Couldn't read" : Join(" · ", os.ActivationStatus, os.LicenseChannel)!),
            new("Pending restart", os.PendingReboot ? $"Yes ({string.Join(", ", os.PendingRebootReasons)})"
                : os.PendingFileRenames ? "No (some files are queued for replacement at the next restart)" : "No"),
            new("Locale / time zone", Join(" · ", os.Locale, os.TimeZone) ?? "?"),
        };
        return fields;
    }

    /// <summary>Plain text for pasting into a ticket ("Copy summary").</summary>
    public static string PlainText(ScanResult scan)
    {
        var model = Build(scan);
        var sb = new StringBuilder();
        sb.AppendLine(model.Title);
        if (model.Subtitle is not null)
        {
            sb.AppendLine(model.Subtitle);
        }

        foreach (var f in model.Identity.Where(f => f.Label is "Serial number" or "SKU / product number"))
        {
            sb.AppendLine($"{f.Label}: {f.Value}");
        }

        sb.AppendLine();
        foreach (var tile in model.Specs)
        {
            sb.AppendLine(tile.Detail is null ? $"{tile.Label}: {tile.Value}" : $"{tile.Label}: {tile.Value} ({tile.Detail})");
        }

        foreach (var v in model.Volumes)
        {
            sb.AppendLine($"  {v.Letter} {v.Detail}");
        }

        sb.AppendLine();
        if (model.Findings.Count == 0)
        {
            sb.AppendLine("Findings: none in the checks so far.");
        }
        else
        {
            sb.AppendLine("Findings:");
            foreach (var f in model.Findings)
            {
                sb.AppendLine($"  [{f.Severity}] {f.Message}");
            }
        }

        sb.AppendLine();
        sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"Scanned by Unravel on {Formatting.DateTime(scan.StartedAt)}");
        return sb.ToString();
    }

    private static string? Join(string separator, params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return present.Count == 0 ? null : string.Join(separator, present);
    }
}

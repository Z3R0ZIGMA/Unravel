using Unravel.Core.Infrastructure;
using Unravel.Core.Models;

namespace Unravel.Core.Collectors;

/// <summary>
/// Physical disks (MSFT_PhysicalDisk / MSFT_Disk, falling back to Win32_DiskDrive) and volumes with free space
/// (Win32_LogicalDisk). SMART data and BitLocker come in a later phase.
/// </summary>
public sealed class StorageCollector : CollectorBase<StorageInfo>
{
    public override string Section => Sections.Storage;

    protected override StorageInfo Collect(CollectContext ctx, CollectorNotes notes, CancellationToken ct)
    {
        var systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\').ToUpperInvariant();

        var disks = ReadStorageDisks(ctx, notes);
        ct.ThrowIfCancellationRequested();
        if (disks.Count == 0)
        {
            disks = ReadLegacyDisks(ctx, notes);
        }

        var volumes = TryQuery(ctx, notes, "volumes", WmiNamespaces.Cimv2, "Win32_LogicalDisk",
                ["DeviceID", "VolumeName", "FileSystem", "Size", "FreeSpace", "DriveType"])
            // 2 = removable, 3 = local disk. Network drives and optical drives are left out.
            .Where(r => r.Int("DriveType") is 2 or 3 && r.ULong("Size") is > 0)
            .Select(r =>
            {
                var letter = r.RawString("DeviceID") ?? "?";
                return new VolumeInfo
                {
                    Letter = letter,
                    Label = r.RawString("VolumeName"),
                    FileSystem = r.RawString("FileSystem"),
                    SizeBytes = r.ULong("Size") ?? 0,
                    FreeBytes = r.ULong("FreeSpace") ?? 0,
                    IsRemovable = r.Int("DriveType") == 2,
                    IsSystemDrive = string.Equals(letter, systemDrive, StringComparison.OrdinalIgnoreCase),
                };
            })
            .OrderBy(v => v.Letter, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (disks.Count == 0 && volumes.Count == 0)
        {
            throw new InvalidOperationException(notes.Warnings.LastOrDefault() ?? "WMI returned no disks");
        }

        return new StorageInfo { Disks = disks, Volumes = volumes };
    }

    private static List<PhysicalDiskInfo> ReadStorageDisks(CollectContext ctx, CollectorNotes notes)
    {
        var physical = TryQuery(ctx, notes, "physical disks", WmiNamespaces.Storage, "MSFT_PhysicalDisk",
            ["DeviceId", "FriendlyName", "Model", "SerialNumber", "MediaType", "BusType", "Size", "HealthStatus"]);
        if (physical.Count == 0)
        {
            return [];
        }

        // MSFT_Disk has the partition style and which disk Windows boots from; Number matches PhysicalDisk.DeviceId.
        var logical = TryQuery(ctx, notes, "partition style", WmiNamespaces.Storage, "MSFT_Disk",
                ["Number", "PartitionStyle", "IsBoot", "IsSystem"])
            .Where(d => d.Int("Number") is not null)
            .GroupBy(d => d.Int("Number")!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var smart = ReadReliability(ctx, notes);
        var predicted = ReadFailurePrediction(ctx);

        return physical.Select(p =>
        {
            var number = int.TryParse(p.RawString("DeviceId"), out var n) ? n : (int?)null;
            var disk = number is { } k && logical.TryGetValue(k, out var d) ? d : null;
            var counters = p.RawString("DeviceId") is { } id ? smart.GetValueOrDefault(id) : null;
            return new PhysicalDiskInfo
            {
                TemperatureC = counters?.Int("Temperature") is > 0 and < 150 and var t ? t : null,
                WearPercent = counters?.Int("Wear") is >= 0 and <= 100 and var w ? w : null,
                PowerOnHours = counters?.Long("PowerOnHours") is > 0 and var h ? h : null,
                ReadErrors = counters?.Long("ReadErrorsUncorrected"),
                WriteErrors = counters?.Long("WriteErrorsUncorrected"),
                // Failure prediction instances cannot be matched to a disk reliably; one disk is the common case.
                PredictedFailure = physical.Count == 1 ? predicted : predicted == true ? true : null,
                Number = number,
                Model = p.String("FriendlyName") ?? p.String("Model"),
                SerialNumber = p.String("SerialNumber"),
                MediaType = StorageRules.MediaType(p.Int("MediaType")),
                BusType = StorageRules.BusType(p.Int("BusType")),
                SizeBytes = p.ULong("Size"),
                Health = StorageRules.Health(p.Int("HealthStatus")),
                PartitionStyle = StorageRules.PartitionStyle(disk?.Int("PartitionStyle")),
                IsSystemDisk = disk?.Bool("IsBoot") == true || disk?.Bool("IsSystem") == true,
            };
        }).OrderBy(d => d.Number ?? int.MaxValue).ToList();
    }

    /// <summary>
    /// SMART-style counters (temperature, wear, power-on hours, uncorrected errors). Needs administrator rights;
    /// many USB and some RAID drives report nothing.
    /// </summary>
    private static Dictionary<string, WmiRecord> ReadReliability(CollectContext ctx, CollectorNotes notes)
    {
        if (!ctx.IsAdmin)
        {
            notes.NeedAdmin();
            return [];
        }

        return TryQuery(ctx, new CollectorNotes(), "disk reliability", WmiNamespaces.Storage, "MSFT_StorageReliabilityCounter",
                ["DeviceId", "Temperature", "Wear", "PowerOnHours", "ReadErrorsUncorrected", "WriteErrorsUncorrected"])
            .Where(r => r.RawString("DeviceId") is not null)
            .GroupBy(r => r.RawString("DeviceId")!)
            .ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>True when any drive's own SMART logic predicts a failure (root\wmi, admin). Null when unknown.</summary>
    private static bool? ReadFailurePrediction(CollectContext ctx)
    {
        if (!ctx.IsAdmin)
        {
            return null;
        }

        var rows = TryQuery(ctx, new CollectorNotes(), "failure prediction", WmiNamespaces.Wmi, "MSStorageDriver_FailurePredictStatus",
            ["InstanceName", "PredictFailure"]);
        return rows.Count == 0 ? null : rows.Any(r => r.Bool("PredictFailure") == true);
    }

    /// <summary>Fallback for machines where the Storage namespace is missing or broken.</summary>
    private static List<PhysicalDiskInfo> ReadLegacyDisks(CollectContext ctx, CollectorNotes notes) =>
        TryQuery(ctx, notes, "disk drives", WmiNamespaces.Cimv2, "Win32_DiskDrive",
                ["Index", "Model", "SerialNumber", "Size", "InterfaceType", "Status"])
            .Select(d => new PhysicalDiskInfo
            {
                Number = d.Int("Index"),
                Model = d.String("Model"),
                SerialNumber = d.String("SerialNumber"),
                BusType = d.String("InterfaceType") switch
                {
                    "IDE" or "SCSI" => null, // NVMe and SATA both report these; not useful
                    var other => other,
                },
                SizeBytes = d.ULong("Size"),
                Health = d.RawString("Status") switch
                {
                    "OK" => DiskHealth.Healthy,
                    "Pred Fail" => DiskHealth.Unhealthy,
                    "Degraded" => DiskHealth.Warning,
                    _ => DiskHealth.Unknown,
                },
            })
            .OrderBy(d => d.Number ?? int.MaxValue)
            .ToList();
}

/// <summary>Pure storage code tables (MSFT_PhysicalDisk / MSFT_Disk).</summary>
public static class StorageRules
{
    /// <summary>MSFT_PhysicalDisk.MediaType: 3 = HDD, 4 = SSD, 5 = SCM, 0 = unspecified.</summary>
    public static string? MediaType(int? code) => code switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => null,
    };

    /// <summary>MSFT_PhysicalDisk.BusType: 17 = NVMe, 11 = SATA, 7 = USB...</summary>
    public static string? BusType(int? code) => code switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "FireWire",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "Virtual",
        15 => "File-backed virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => null,
    };

    /// <summary>MSFT_PhysicalDisk.HealthStatus: 0 = Healthy, 1 = Warning, 2 = Unhealthy, 5 = Unknown.</summary>
    public static DiskHealth Health(int? code) => code switch
    {
        0 => DiskHealth.Healthy,
        1 => DiskHealth.Warning,
        2 => DiskHealth.Unhealthy,
        _ => DiskHealth.Unknown,
    };

    /// <summary>MSFT_Disk.PartitionStyle: 1 = MBR, 2 = GPT, 0 = RAW/unknown.</summary>
    public static string? PartitionStyle(int? code) => code switch
    {
        0 => "RAW",
        1 => "MBR",
        2 => "GPT",
        _ => null,
    };
}

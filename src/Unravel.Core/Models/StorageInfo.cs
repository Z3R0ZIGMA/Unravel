namespace Unravel.Core.Models;

public enum DiskHealth
{
    Unknown,
    Healthy,
    Warning,
    Unhealthy,
}

/// <summary>One physical disk (plan.md section 4.3, basic level).</summary>
public sealed record PhysicalDiskInfo
{
    /// <summary>Disk number as in Disk Management (0, 1...).</summary>
    public int? Number { get; init; }

    public string? Model { get; init; }

    public string? SerialNumber { get; init; }

    /// <summary>"SSD", "HDD", "SCM" or null when Windows does not know.</summary>
    public string? MediaType { get; init; }

    /// <summary>"NVMe", "SATA", "USB", "SAS", "RAID"...</summary>
    public string? BusType { get; init; }

    public ulong? SizeBytes { get; init; }

    public DiskHealth Health { get; init; }

    /// <summary>"GPT", "MBR", "RAW" or null.</summary>
    public string? PartitionStyle { get; init; }

    /// <summary>Windows boots from this disk (holds C:).</summary>
    public bool IsSystemDisk { get; init; }

    public bool IsUsb => string.Equals(BusType, "USB", StringComparison.OrdinalIgnoreCase);

    /// <summary>"NVMe SSD", "SATA HDD", "USB"...</summary>
    public string Kind => (MediaType, BusType) switch
    {
        ("SSD", "NVMe") => "NVMe SSD",
        (null, "NVMe") => "NVMe SSD",
        (var m, var b) when m is not null && b is not null => $"{b} {m}",
        (var m, null) when m is not null => m,
        (null, var b) when b is not null => b,
        _ => "Disk",
    };
}

/// <summary>One volume with a drive letter.</summary>
public sealed record VolumeInfo
{
    /// <summary>"C:".</summary>
    public required string Letter { get; init; }

    public string? Label { get; init; }

    public string? FileSystem { get; init; }

    public ulong SizeBytes { get; init; }

    public ulong FreeBytes { get; init; }

    public bool IsRemovable { get; init; }

    public bool IsSystemDrive { get; init; }

    public ulong UsedBytes => SizeBytes > FreeBytes ? SizeBytes - FreeBytes : 0;

    public double PercentUsed => SizeBytes == 0 ? 0 : 100.0 * UsedBytes / SizeBytes;

    public double PercentFree => SizeBytes == 0 ? 0 : 100.0 * FreeBytes / SizeBytes;
}

public sealed record StorageInfo
{
    public IReadOnlyList<PhysicalDiskInfo> Disks { get; init; } = [];

    public IReadOnlyList<VolumeInfo> Volumes { get; init; } = [];

    public PhysicalDiskInfo? SystemDisk => Disks.FirstOrDefault(d => d.IsSystemDisk);

    public VolumeInfo? SystemVolume => Volumes.FirstOrDefault(v => v.IsSystemDrive);

    /// <summary>Internal disks only (USB sticks and external drives excluded).</summary>
    public IEnumerable<PhysicalDiskInfo> InternalDisks => Disks.Where(d => !d.IsUsb);
}

using Unravel.Core.Models;

namespace Unravel.Core.Diagnostics;

public enum Severity
{
    Ok,
    Info,
    Warning,
    Critical,
}

/// <summary>A health check result: what is wrong, how bad, and what to do about it (plan.md section 5.1).</summary>
public sealed record Finding(Severity Severity, string Area, string Message, string? Recommendation = null);

/// <summary>
/// The passive checks that Phase 1 data supports (storage, memory, uptime, activation, pending reboot). The full rules
/// engine with the remaining checks from plan.md section 5.1 comes in Phase 3.
/// </summary>
public static class HealthRules
{
    public const string StorageArea = "Storage";
    public const string MemoryArea = "Memory";
    public const string OsArea = "Windows";
    public const string UpdatesArea = "Updates";

    public static IReadOnlyList<Finding> Evaluate(ScanResult scan) =>
        [.. Storage(scan.Storage), .. Memory(scan.Memory), .. Os(scan.Os)];

    public static IEnumerable<Finding> Storage(StorageInfo? storage)
    {
        if (storage is null)
        {
            yield break;
        }

        foreach (var volume in storage.Volumes.Where(v => v.IsSystemDrive))
        {
            var free = volume.PercentFree;
            var text = $"Drive {volume.Letter} is {Formatting.Percent(volume.PercentUsed)} full ({Formatting.VolumeSize(volume.FreeBytes)} free)";
            if (free < 5)
            {
                yield return new(Severity.Critical, StorageArea, text, "Free up space now: Windows updates and apps fail when the system drive is full.");
            }
            else if (free < 15)
            {
                yield return new(Severity.Warning, StorageArea, text, "Free up space (Disk Cleanup, Storage Sense) or move data off the drive.");
            }
        }

        foreach (var disk in storage.Disks)
        {
            if (disk.Health is DiskHealth.Unhealthy or DiskHealth.Warning)
            {
                yield return new(Severity.Critical, StorageArea,
                    $"Disk {disk.Number} ({disk.Model ?? disk.Kind}) reports health \"{disk.Health}\"",
                    "Back up the data and plan to replace the disk.");
            }
        }

        if (storage.SystemDisk is { MediaType: "HDD" } hdd)
        {
            yield return new(Severity.Info, StorageArea, $"Windows runs from a hard disk ({hdd.Model ?? "HDD"})",
                "An SSD upgrade would make the PC much faster.");
        }
    }

    public static IEnumerable<Finding> Memory(MemoryInfo? memory)
    {
        if (memory is null)
        {
            yield break;
        }

        const ulong eightGb = 8UL * 1024 * 1024 * 1024;
        // 7.8 GB usable of 8 GB installed is still 8 GB: judge on installed memory.
        if (memory.InstalledBytes > 0 && memory.InstalledBytes < eightGb - 256UL * 1024 * 1024)
        {
            yield return new(Severity.Warning, MemoryArea, $"Only {Formatting.Memory(memory.InstalledBytes)} of memory installed",
                MemoryUpgradeText(memory) ?? "8 GB is the practical minimum for Windows 11 and Microsoft 365.");
        }

        if (memory.RunningBelowRated)
        {
            var m = memory.Modules.First(x => x.RunningSpeed < x.RatedSpeed);
            yield return new(Severity.Info, MemoryArea, $"Memory runs at {m.RunningSpeed} MT/s, below its rated {m.RatedSpeed} MT/s",
                "Often normal (the CPU or board limits the speed). On desktops, check XMP/EXPO in the BIOS.");
        }

        if (memory.SingleChannel)
        {
            yield return new(Severity.Info, MemoryArea, "One memory module: memory runs in single-channel mode",
                "A second matching module would double memory bandwidth" + (memory.SlotsFree is > 0 ? " (a free slot is available)." : "."));
        }
        else if (memory.Mismatched)
        {
            yield return new(Severity.Info, MemoryArea, "Memory modules have different sizes",
                "Part of the memory may run in single-channel mode. Matching pairs perform best.");
        }
    }

    public static IEnumerable<Finding> Os(OsInfo? os)
    {
        if (os is null)
        {
            yield break;
        }

        if (os.IsActivated == false)
        {
            yield return new(Severity.Warning, OsArea, $"Windows is not activated ({os.ActivationStatus})",
                "Activate Windows in Settings > System > Activation.");
        }

        if (os.PendingReboot)
        {
            yield return new(Severity.Warning, UpdatesArea, $"Restart pending for {string.Join(" and ", os.PendingRebootReasons)}",
                "Restart the PC to finish installing updates.");
        }

        if (os.Uptime is { TotalDays: > 14 } up)
        {
            yield return new(Severity.Info, OsArea, $"Not restarted for {Formatting.Duration(up)}",
                "Restart to apply updates and clear memory. With Fast Startup on, shutting down does not count as a restart.");
        }
    }

    /// <summary>"1 of 2 slots free, supports up to 64 GB", or null when the firmware does not say.</summary>
    public static string? MemoryUpgradeText(MemoryInfo memory)
    {
        if (memory.AllSoldered)
        {
            return "Memory is soldered to the board and cannot be upgraded.";
        }

        var parts = new List<string>();
        if (memory.TotalSlots is { } total)
        {
            parts.Add(memory.SlotsFree is > 0 and var free ? $"{free} of {total} slots free" : $"all {total} slots used");
        }

        if (memory.MaxCapacityBytes is { } max && max >= memory.InstalledBytes)
        {
            parts.Add($"supports up to {Formatting.Memory(max)}");
        }

        return parts.Count == 0 ? null : char.ToUpperInvariant(parts[0][0]) + string.Join(", ", parts)[1..];
    }
}

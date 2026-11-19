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
/// The passive checks the collected data supports so far (plan.md section 5.1): storage, memory, Windows, security,
/// battery, firmware and devices. The full rules engine (update history, event log, Win11 readiness) comes in Phase 3.
/// </summary>
public static class HealthRules
{
    public const string StorageArea = "Storage";
    public const string MemoryArea = "Memory";
    public const string OsArea = "Windows";
    public const string UpdatesArea = "Updates";
    public const string SecurityArea = "Security";
    public const string BatteryArea = "Battery";
    public const string FirmwareArea = "Firmware";
    public const string DevicesArea = "Devices";
    public const string StabilityArea = "Stability";
    public const string Win11Area = "Windows 11";

    public static IReadOnlyList<Finding> Evaluate(ScanResult scan) =>
    [
        .. Storage(scan.Storage), .. Memory(scan.Memory), .. Os(scan.Os), .. Support(scan.Os, scan.StartedAt),
        .. Updates(scan.Updates, scan.Os, scan.StartedAt), .. Security(scan.Security, scan.Device, scan.Storage),
        .. Battery(scan.Battery), .. Firmware(scan.Firmware, scan.StartedAt), .. Devices(scan.Drivers), .. Software(scan.Software),
        .. Stability(scan.Stability), .. Windows11(scan),
    ];

    /// <summary>End of support from the lifecycle table (plan.md 4.4 / 5.1: out-of-support build is Critical).</summary>
    public static IEnumerable<Finding> Support(OsInfo? os, DateTimeOffset now)
    {
        if (os is null)
        {
            yield break;
        }

        var status = Lifecycle.Current.Check(os, now);
        switch (status.State)
        {
            case SupportState.OutOfSupport:
                yield return new(Severity.Critical, OsArea, $"{os.Name ?? "Windows"} {os.DisplayVersion} is out of support",
                    (os.Build is < 22000 ? "Upgrade to Windows 11" : "Update to a current Windows 11 version")
                    + (status.Note is { } note ? $". {note}" : ": it no longer gets security updates."));
                break;
            case SupportState.EndingSoon:
                yield return new(Severity.Info, OsArea, $"{os.Name ?? "Windows"} {os.DisplayVersion} support ends on {Formatting.Date(status.EndDate?.ToDateTime(TimeOnly.MinValue))}",
                    "Plan the feature update before then.");
                break;
        }
    }

    public static IEnumerable<Finding> Updates(UpdatesInfo? updates, OsInfo? os, DateTimeOffset now)
    {
        if (updates is null)
        {
            yield break;
        }

        if (string.Equals(updates.ServiceStartMode, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            yield return new(Severity.Warning, UpdatesArea, "The Windows Update service is disabled",
                "Set the Windows Update service back to Manual so updates can install (unless updates are managed another way).");
        }

        var last = updates.LastQualityUpdate ?? updates.LastInstalled;
        if (last is null && updates.History.Count == 0)
        {
            yield return new(Severity.Warning, UpdatesArea, "No Windows Update history found", "Check Windows Update in Settings.");
        }
        else if (last is not null && (now - last.Date).TotalDays > 45)
        {
            yield return new(Severity.Warning, UpdatesArea, $"Last {(updates.LastQualityUpdate is null ? "update" : "monthly update")} installed {(int)(now - last.Date).TotalDays} days ago",
                "Run Windows Update: monthly security updates are missing.");
        }

        foreach (var failed in updates.RecentFailures.Where(f => (now - f.Date).TotalDays <= 30).Take(3))
        {
            yield return new(Severity.Info, UpdatesArea, $"Update failed: {failed.Title}", "Check Windows Update; it usually retries on its own.");
        }
    }

    /// <summary>Event log and stability over the last 30 days (plan.md 5.2: Warning when repeated).</summary>
    public static IEnumerable<Finding> Stability(StabilityInfo? s)
    {
        if (s is null)
        {
            yield break;
        }

        if (s.BlueScreens.Count > 0)
        {
            var latest = s.BlueScreens.OrderByDescending(b => b.Time).First();
            yield return new(s.BlueScreens.Count > 1 ? Severity.Warning : Severity.Info, StabilityArea,
                s.BlueScreens.Count == 1 ? $"One blue screen in the last {s.Days} days ({latest.Detail})" : $"{s.BlueScreens.Count} blue screens in the last {s.Days} days (latest: {latest.Detail})",
                "Update drivers and BIOS; repeated blue screens often point to a driver, memory or disk problem.");
        }

        if (s.UnexpectedShutdowns.Count > 0)
        {
            yield return new(s.UnexpectedShutdowns.Count > 1 ? Severity.Warning : Severity.Info, StabilityArea,
                $"{s.UnexpectedShutdowns.Count} unexpected shutdown{(s.UnexpectedShutdowns.Count == 1 ? "" : "s")} in the last {s.Days} days",
                "Check power (battery, adapter, power supply) and overheating; ask whether the PC was switched off at the button.");
        }

        var realDiskErrors = s.DiskErrors.Where(e => !e.IsRetryOnly).ToList();
        var bursts = s.DiskErrorBursts;
        if (realDiskErrors.Count > 0)
        {
            yield return new(Severity.Warning, StabilityArea, $"{realDiskErrors.Count} disk read errors in the last {s.Days} days ({realDiskErrors[0].Detail})",
                "Back up the data and check the disk (Hardware > Storage, and the SMART data as administrator).");
        }
        else if (bursts.Count >= 3)
        {
            yield return new(Severity.Warning, StabilityArea, $"Disk I/O retries in {bursts.Count} bursts in the last {s.Days} days ({s.DiskErrors.Count} events)",
                "Often harmless after sleep, but frequent retries can mean a failing disk, cable or storage driver: update the storage driver and BIOS.");
        }
        else if (bursts.Count > 0)
        {
            yield return new(Severity.Info, StabilityArea, $"Disk I/O retries in {bursts.Count} burst{(bursts.Count == 1 ? "" : "s")} in the last {s.Days} days",
                "Usually harmless (often logged when resuming from sleep).");
        }

        var uncorrected = s.HardwareErrors.Where(e => !e.Detail.StartsWith("Corrected", StringComparison.Ordinal)).ToList();
        if (uncorrected.Count > 0)
        {
            yield return new(Severity.Warning, StabilityArea, $"{uncorrected.Count} hardware errors (WHEA) in the last {s.Days} days",
                "WHEA errors often point to memory, CPU or overheating: run the memory test and check temperatures.");
        }
        else if (s.HardwareErrors.Count >= 10)
        {
            yield return new(Severity.Info, StabilityArea, $"{s.HardwareErrors.Count} corrected hardware errors (WHEA) in the last {s.Days} days",
                "Corrected errors are handled by the hardware, but a high count can be an early sign of a problem.");
        }

        foreach (var crash in s.AppCrashes.Where(c => c.Count >= 3).Take(3))
        {
            yield return new(crash.Count >= 10 ? Severity.Warning : Severity.Info, StabilityArea,
                $"{crash.App} crashed {crash.Count} times in the last {s.Days} days",
                crash.Module is { } m && m != "unknown" ? $"Faulting module: {m}. Update or repair the app." : "Update or repair the app.");
        }

        if (s.LastMemoryTest is { ErrorsFound: true } memoryTest)
        {
            yield return new(Severity.Critical, MemoryArea, $"Windows Memory Diagnostic found errors ({Formatting.Date(memoryTest.Time)})",
                "Replace the faulty memory module.");
        }
    }

    /// <summary>Windows 11 readiness (plan.md 5.3), as a finding only where it matters: Windows 10 PCs.</summary>
    public static IEnumerable<Finding> Windows11(ScanResult scan)
    {
        if (scan.Os is not { Build: < 22000 } || scan.Cpu is null || scan.Firmware is null)
        {
            yield break;
        }

        var readiness = Win11Check.Evaluate(scan);
        switch (readiness.State)
        {
            case ReadinessState.NotReady:
                yield return new(Severity.Warning, Win11Area, readiness.Summary,
                    string.Join(" ", readiness.Blockers.Select(b => b.Fix).OfType<string>().Distinct()));
                break;
            case ReadinessState.Ready:
                yield return new(Severity.Info, Win11Area, "This PC can be upgraded to Windows 11", "Upgrade through Windows Update or the Installation Assistant.");
                break;
        }
    }

    public static IEnumerable<Finding> Security(SecurityInfo? security, DeviceInfo? device, StorageInfo? storage)
    {
        if (security is null)
        {
            yield break;
        }

        var activeAv = security.Antivirus.Where(a => a.Enabled == true).ToList();
        if (!security.HasActiveAntivirus)
        {
            yield return new(Severity.Critical, SecurityArea, "No active antivirus protection",
                "Turn on Microsoft Defender real-time protection or install a supported antivirus.");
        }
        else if (activeAv.Count(a => !a.IsDefender) > 1)
        {
            yield return new(Severity.Warning, SecurityArea, $"More than one antivirus is active ({string.Join(", ", activeAv.Select(a => a.Name))})",
                "Two real-time scanners conflict and slow the PC down. Keep one.");
        }

        foreach (var av in activeAv.Where(a => a.UpToDate == false))
        {
            yield return new(Severity.Critical, SecurityArea, $"{av.Name} definitions are out of date", "Update the antivirus definitions.");
        }

        if (security.Defender is { RealTimeProtection: true, SignatureAgeDays: > 7 } d)
        {
            yield return new(Severity.Critical, SecurityArea, $"Microsoft Defender definitions are {d.SignatureAgeDays} days old",
                "Run Windows Update or update Defender definitions.");
        }

        foreach (var profile in security.Firewall.Where(p => p.Enabled == false))
        {
            yield return new(Severity.Warning, SecurityArea, $"Firewall is off for the {profile.Name} profile",
                profile.SetByPolicy ? "Turned off by Group Policy: check the policy." : "Turn the firewall back on in Windows Security.");
        }

        var laptop = device?.Chassis is ChassisKind.Laptop or ChassisKind.Convertible or ChassisKind.Tablet;
        var system = security.BitLocker?.FirstOrDefault(v => v.IsSystemVolume)
                     ?? (storage?.SystemVolume is { } sv ? security.BitLocker?.FirstOrDefault(v => v.Volume == sv.Letter) : null);
        if (laptop && system is { ProtectionOn: false })
        {
            yield return new(Severity.Warning, SecurityArea, $"BitLocker is off on drive {system.Volume} of a laptop",
                "Turn on BitLocker (or device encryption) so the data is safe if the laptop is lost.");
        }

        if (security.UacEnabled == false)
        {
            yield return new(Severity.Warning, SecurityArea, "User Account Control is turned off", "Turn UAC back on.");
        }
    }

    public static IEnumerable<Finding> Battery(BatteryInfo? battery)
    {
        if (battery?.Primary is not { HealthPercent: { } health } b)
        {
            yield break;
        }

        var text = $"Battery holds {health:0}% of its original capacity";
        if (health < 50)
        {
            yield return new(Severity.Critical, BatteryArea, text, "Replace the battery. Open Unveil for the full battery check.");
        }
        else if (health < 80)
        {
            yield return new(Severity.Warning, BatteryArea, text + (b.CycleCount is { } c ? $" after {c} cycles" : ""),
                "Battery life is noticeably shorter. Open Unveil for the full battery check.");
        }
    }

    public static IEnumerable<Finding> Firmware(FirmwareInfo? firmware, DateTimeOffset now)
    {
        if (firmware is null)
        {
            yield break;
        }

        if (firmware is { BootMode: "UEFI", SecureBoot: false })
        {
            yield return new(Severity.Warning, FirmwareArea, "Secure Boot is off", "Turn on Secure Boot in the UEFI settings (needed for Windows 11).");
        }

        if (firmware.Tpm is { Present: false })
        {
            yield return new(Severity.Warning, FirmwareArea, "No TPM found", "Enable the TPM (PTT / fTPM) in the UEFI settings if the board has one.");
        }

        if (firmware.BiosAgeYears(now) is > 3 and var age)
        {
            yield return new(Severity.Info, FirmwareArea, $"BIOS is {age:0} years old ({Formatting.Date(firmware.BiosDate)})",
                "Check the manufacturer's site for a firmware update.");
        }
    }

    public static IEnumerable<Finding> Devices(DriverInfo? drivers)
    {
        var broken = drivers?.ProblemDevices.Where(d => !d.IsDisabled).ToList() ?? [];
        if (broken.Count > 0)
        {
            yield return new(Severity.Warning, DevicesArea,
                broken.Count == 1 ? $"Device problem: {broken[0].Name}" : $"{broken.Count} devices have problems ({string.Join(", ", broken.Take(3).Select(d => d.Name))})",
                "Install or update the driver (see Hardware > Devices and drivers).");
        }
    }

    public static IEnumerable<Finding> Software(SoftwareInfo? software)
    {
        var remote = software?.RemoteAccess.Select(r => r.RemoteAccessTool).Distinct().ToList() ?? [];
        if (remote.Count > 0)
        {
            yield return new(Severity.Info, SecurityArea, $"Remote access tools installed: {string.Join(", ", remote)}",
                "Check each one is expected and still in use.");
        }
    }

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

        foreach (var disk in storage.Disks)
        {
            var label = $"Disk {disk.Number} ({disk.Model ?? disk.Kind})";
            if (disk.PredictedFailure == true)
            {
                yield return new(Severity.Critical, StorageArea, $"SMART predicts a failure on {(storage.Disks.Count == 1 ? label : "a disk")}",
                    "Back up now and replace the disk.");
            }

            if (disk.WearPercent is > 80 and var wear)
            {
                yield return new(Severity.Warning, StorageArea, $"{label} is {wear}% worn", "Plan to replace the SSD and keep the backup current.");
            }

            if (disk.ReadErrors is > 0 || disk.WriteErrors is > 0)
            {
                yield return new(Severity.Warning, StorageArea, $"{label} reports uncorrected read/write errors ({disk.ReadErrors ?? 0} read, {disk.WriteErrors ?? 0} write)",
                    "Back up the data and check the disk.");
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

        // Plan.md 5.1: a restart pending for more than 7 days is a Warning. Windows does not record when it became
        // pending, so the uptime is the measure: no restart for 7 days means it has waited at least that long.
        if (os.PendingReboot)
        {
            var waiting = os.Uptime is { TotalDays: > 7 } pendingFor ? $", not restarted for {Formatting.Duration(pendingFor)}" : "";
            yield return new(waiting.Length > 0 ? Severity.Warning : Severity.Info, UpdatesArea,
                $"Restart pending for {string.Join(" and ", os.PendingRebootReasons)}{waiting}",
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

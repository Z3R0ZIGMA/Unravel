using System.Text.RegularExpressions;
using Unravel.Core.Models;

namespace Unravel.Core.Diagnostics;

public enum CheckResult
{
    Pass,
    Fail,
    Unknown,
}

public sealed record ReadinessCheck(string Requirement, CheckResult Result, string Detail, string? Fix = null);

public enum ReadinessState
{
    Ready,
    NotReady,
    /// <summary>Nothing fails, but at least one requirement could not be checked.</summary>
    Unknown,
}

public sealed record Win11Readiness(ReadinessState State, IReadOnlyList<ReadinessCheck> Checks, bool AlreadyWindows11)
{
    public IEnumerable<ReadinessCheck> Blockers => Checks.Where(c => c.Result == CheckResult.Fail);

    public string Summary => (State, AlreadyWindows11) switch
    {
        (ReadinessState.Ready, true) => "Runs Windows 11 on supported hardware",
        (ReadinessState.Ready, false) => "Ready for Windows 11",
        (ReadinessState.NotReady, true) => "Runs Windows 11 on unsupported hardware",
        (ReadinessState.NotReady, false) => $"Not ready for Windows 11: {string.Join(", ", Blockers.Select(b => b.Requirement))}",
        _ => "Windows 11 readiness unknown: some requirements could not be checked",
    };
}

/// <summary>
/// Windows 11 hardware requirements (plan.md section 5.3): supported CPU, TPM 2.0, Secure Boot capable, UEFI,
/// 4 GB RAM, 64 GB storage, GPT system disk.
/// </summary>
public static partial class Win11Check
{
    private const ulong FourGb = 4UL * 1024 * 1024 * 1024;
    private const ulong SixtyFourGb = 64UL * 1000 * 1000 * 1000;

    public static Win11Readiness Evaluate(ScanResult scan)
    {
        var checks = new List<ReadinessCheck>
        {
            Cpu(scan.Cpu),
            Tpm(scan.Firmware),
            Uefi(scan.Firmware),
            SecureBoot(scan.Firmware),
            Memory(scan.Memory),
            Storage(scan.Storage),
            Gpt(scan.Storage),
        };

        var state = checks.Any(c => c.Result == CheckResult.Fail) ? ReadinessState.NotReady
            : checks.Any(c => c.Result == CheckResult.Unknown) ? ReadinessState.Unknown
            : ReadinessState.Ready;
        return new Win11Readiness(state, checks, scan.Os is { Build: >= 22000 });
    }

    private static ReadinessCheck Cpu(CpuInfo? cpu)
    {
        if (cpu?.Primary is not { } p)
        {
            return new("Processor", CheckResult.Unknown, "Couldn't read the processor");
        }

        var (result, why) = CpuSupported(p);
        return new("Processor", result, $"{p.Name}: {why}",
            result == CheckResult.Fail ? "The processor is too old for Windows 11; plan a replacement PC." : null);
    }

    /// <summary>
    /// Microsoft's supported-CPU lists, by family and generation: Intel 8th gen Core and later, AMD Ryzen 2000 (Zen+)
    /// and later, Qualcomm Snapdragon 850 and later. Exact models outside these rules come back as Unknown.
    /// </summary>
    public static (CheckResult Result, string Why) CpuSupported(ProcessorInfo p)
    {
        var name = p.Name ?? "";
        bool Has(string s) => name.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (p.Architecture is "x86")
        {
            return (CheckResult.Fail, "32-bit processors are not supported");
        }

        if (Has("Snapdragon") || Has("Microsoft SQ"))
        {
            return Has("835") ? (CheckResult.Fail, "Snapdragon 835 is not supported") : (CheckResult.Pass, "supported Qualcomm processor");
        }

        if (p.Manufacturer == "Intel" || Has("Intel"))
        {
            if (Has("Core Ultra") || Has("Core(TM) Ultra") || (p.Generation?.Contains("Series", StringComparison.Ordinal) == true))
            {
                return (CheckResult.Pass, "Intel Core Ultra / Core Series");
            }

            if (Has("i7-7820HQ"))
            {
                return (CheckResult.Pass, "on Microsoft's list as an exception (Surface Studio 2)");
            }

            if (p.GenerationNumber is { } gen)
            {
                return gen >= 8 ? (CheckResult.Pass, $"{p.Generation} (8th gen or later)") : (CheckResult.Fail, $"{p.Generation}: Windows 11 needs 8th gen or later");
            }

            if (IntelNSeries().IsMatch(name) || IntelLowEndSupported().IsMatch(name))
            {
                return (CheckResult.Pass, "supported Pentium / Celeron / N-series model");
            }

            if (Has("Celeron") || Has("Pentium") || Has("Atom"))
            {
                return (CheckResult.Fail, "older Pentium / Celeron / Atom models are not supported");
            }

            if (XeonSupported().IsMatch(name))
            {
                return (CheckResult.Pass, "supported Xeon generation");
            }

            return (CheckResult.Unknown, "check this model against Microsoft's supported Intel processor list");
        }

        if (p.Manufacturer == "AMD" || Has("AMD"))
        {
            if (Has("Ryzen AI") || p.GenerationNumber is >= 3000)
            {
                return (CheckResult.Pass, p.Generation ?? "Ryzen 3000 or later");
            }

            if (p.GenerationNumber == 2000)
            {
                // Ryzen 2000 desktop chips are Zen+ (supported); 2000 mobile and the 2200G/2400G APUs are Zen (not).
                return RyzenZen1In2000().IsMatch(name)
                    ? (CheckResult.Fail, "this Ryzen 2000 model is first-generation Zen")
                    : (CheckResult.Pass, "Ryzen 2000 desktop (Zen+)");
            }

            if (p.GenerationNumber == 1000)
            {
                return (CheckResult.Fail, "Ryzen 1000 (first-generation Zen) is not supported");
            }

            if (Has("Athlon"))
            {
                return AthlonSupported().IsMatch(name) ? (CheckResult.Pass, "supported Athlon model") : (CheckResult.Fail, "older Athlon models are not supported");
            }

            if (Has("EPYC"))
            {
                return EpycFirstGen().IsMatch(name) ? (CheckResult.Fail, "first-generation EPYC is not supported") : (CheckResult.Pass, "EPYC 2nd gen or later");
            }

            if (Has("A4-") || Has("A6-") || Has("A8-") || Has("A9-") || Has("A10-") || Has("A12-") || Has("FX-") || Has("E2-") || Has("Phenom"))
            {
                return (CheckResult.Fail, "pre-Ryzen AMD processors are not supported");
            }

            return (CheckResult.Unknown, "check this model against Microsoft's supported AMD processor list");
        }

        return (CheckResult.Unknown, "unknown processor maker");
    }

    private static ReadinessCheck Tpm(FirmwareInfo? f)
    {
        var tpm = f?.Tpm;
        if (tpm is null)
        {
            return new("TPM 2.0", CheckResult.Unknown, "Couldn't check the TPM");
        }

        if (!tpm.Present)
        {
            return new("TPM 2.0", CheckResult.Fail, "No TPM found", "Turn on the firmware TPM (Intel PTT / AMD fTPM) in the UEFI settings.");
        }

        return tpm.Version switch
        {
            "2.0" => new("TPM 2.0", CheckResult.Pass, "TPM 2.0"),
            "1.2" => new("TPM 2.0", CheckResult.Fail, "TPM 1.2 only", "Some boards can switch to TPM 2.0 with a firmware update."),
            _ => new("TPM 2.0", CheckResult.Unknown, "TPM present, version unknown (restart as administrator)"),
        };
    }

    private static ReadinessCheck Uefi(FirmwareInfo? f) => f?.BootMode switch
    {
        "UEFI" => new("UEFI boot", CheckResult.Pass, "Boots with UEFI"),
        "Legacy BIOS" => new("UEFI boot", CheckResult.Fail, "Boots in legacy BIOS mode",
            "Convert the disk to GPT (mbr2gpt) and switch the firmware to UEFI."),
        _ => new("UEFI boot", CheckResult.Unknown, "Boot mode unknown"),
    };

    private static ReadinessCheck SecureBoot(FirmwareInfo? f) => (f?.BootMode, f?.SecureBoot) switch
    {
        ("UEFI", true) => new("Secure Boot capable", CheckResult.Pass, "Secure Boot is on"),
        // The state value exists only on firmware that supports Secure Boot: capable, just switched off.
        ("UEFI", false) => new("Secure Boot capable", CheckResult.Pass, "Capable (currently off)"),
        ("Legacy BIOS", _) => new("Secure Boot capable", CheckResult.Fail, "Not available in legacy BIOS mode", "Switch the firmware to UEFI."),
        _ => new("Secure Boot capable", CheckResult.Unknown, "Couldn't check Secure Boot"),
    };

    private static ReadinessCheck Memory(MemoryInfo? m) => m is null
        ? new("4 GB memory", CheckResult.Unknown, "Couldn't read the memory")
        : m.InstalledBytes >= FourGb - 64UL * 1024 * 1024
            ? new("4 GB memory", CheckResult.Pass, Formatting.Memory(m.InstalledBytes))
            : new("4 GB memory", CheckResult.Fail, Formatting.Memory(m.InstalledBytes), "Add memory.");

    private static ReadinessCheck Storage(StorageInfo? s)
    {
        var size = s?.SystemDisk?.SizeBytes ?? s?.SystemVolume?.SizeBytes;
        return size is null
            ? new("64 GB storage", CheckResult.Unknown, "Couldn't read the Windows disk")
            : size >= SixtyFourGb
                ? new("64 GB storage", CheckResult.Pass, Formatting.DiskSize(size))
                : new("64 GB storage", CheckResult.Fail, Formatting.DiskSize(size), "A larger disk is needed.");
    }

    private static ReadinessCheck Gpt(StorageInfo? s) => s?.SystemDisk?.PartitionStyle switch
    {
        "GPT" => new("GPT system disk", CheckResult.Pass, "GPT"),
        "MBR" => new("GPT system disk", CheckResult.Fail, "MBR", "Convert with mbr2gpt (back up first)."),
        _ => new("GPT system disk", CheckResult.Unknown, "Partition style unknown"),
    };

    [GeneratedRegex(@"\bIntel\b.*\b(N50|N95|N97|N100|N150|N200|N250|N300|N305|N355|i3-N\d{3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntelNSeries();

    // Gemini Lake Refresh (N4020, N4120, N5030), Jasper Lake (N4500, N4505, N5100, N5105, N6000, N6005, N6211),
    // Comet/Tiger/Alder Lake Pentium Gold and Celeron (6305, 6405U, 7305, 7505, 8505, G6400, G6900, G7400).
    [GeneratedRegex(@"\b(N4020|N4120|N4500|N4505|N5030|N5100|N5105|N6000|N6005|N6210|N6211|[5-8]\d0[05]U?|G6\d{3}T?|G7\d{3}T?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntelLowEndSupported();

    [GeneratedRegex(@"Xeon.*\b(E-2\d{3}|W-1\d{3}|W-22\d{2}|W-32\d{2}|W-3\d{3}|W-2[4-9]\d{2}|(Bronze|Silver|Gold|Platinum) [3-9][2-9]\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex XeonSupported();

    [GeneratedRegex(@"\b2\d{3}(U|H|HS|G|GE)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RyzenZen1In2000();

    [GeneratedRegex(@"Athlon\s+(Gold|Silver|3000G|300U|7\d{3})", RegexOptions.IgnoreCase)]
    private static partial Regex AthlonSupported();

    [GeneratedRegex(@"EPYC\s+7\d{2}1\b", RegexOptions.IgnoreCase)]
    private static partial Regex EpycFirstGen();
}

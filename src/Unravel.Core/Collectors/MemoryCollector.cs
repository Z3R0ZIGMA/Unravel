using Unravel.Core.Infrastructure;
using Unravel.Core.Models;

namespace Unravel.Core.Collectors;

/// <summary>Memory modules, DDR generation and upgrade information (plan.md section 4.3).</summary>
public sealed class MemoryCollector : CollectorBase<MemoryInfo>
{
    public override string Section => Sections.Memory;

    protected override MemoryInfo Collect(CollectContext ctx, CollectorNotes notes, CancellationToken ct)
    {
        var rows = TryQuery(ctx, notes, "memory modules", WmiNamespaces.Cimv2, "Win32_PhysicalMemory",
        [
            "DeviceLocator", "BankLabel", "Capacity", "Speed", "ConfiguredClockSpeed", "Manufacturer", "PartNumber",
            "FormFactor", "SMBIOSMemoryType", "MemoryType",
        ]);
        ct.ThrowIfCancellationRequested();

        var modules = rows
            .Where(r => r.ULong("Capacity") is > 0)
            .Select(MemoryRules.ToModule)
            .ToList();

        var arrays = TryQuery(ctx, notes, "memory slots", WmiNamespaces.Cimv2, "Win32_PhysicalMemoryArray",
            ["MemoryDevices", "MaxCapacity", "MaxCapacityEx", "Use"]);
        // Use = 3 is system memory; other arrays are cache or flash.
        var systemArrays = arrays.Where(a => a.Int("Use") is 3 or null).ToList();
        var slots = systemArrays.Sum(a => a.Int("MemoryDevices") ?? 0);
        var maxKb = systemArrays.Sum(a => (decimal)(a.ULong("MaxCapacityEx") is > 0 and var ex ? ex : a.ULong("MaxCapacity") ?? 0));

        var cs = TryQuery(ctx, notes, "usable memory", WmiNamespaces.Cimv2, "Win32_ComputerSystem", ["TotalPhysicalMemory"]).FirstOrDefault();
        var usable = cs?.ULong("TotalPhysicalMemory");

        if (modules.Count == 0 && usable is null)
        {
            throw new InvalidOperationException(notes.Warnings.LastOrDefault() ?? "WMI returned no memory information");
        }

        var installed = modules.Count > 0 ? modules.Aggregate(0UL, (sum, m) => sum + m.CapacityBytes) : usable ?? 0;
        if (modules.Count == 0)
        {
            notes.Warn("Couldn't read memory modules (the firmware does not report them; total from Windows)");
        }

        return new MemoryInfo
        {
            Modules = modules,
            InstalledBytes = installed,
            UsableBytes = usable,
            TotalSlots = slots > 0 && slots >= modules.Count ? slots : null,
            MaxCapacityBytes = maxKb > 0 ? (ulong)(maxKb * 1024) : null,
        };
    }
}

/// <summary>Pure memory rules: generation, form factor, vendor codes.</summary>
public static class MemoryRules
{
    public static MemoryModule ToModule(WmiRecord r)
    {
        var part = r.String("PartNumber");
        var rated = r.Int("Speed") is > 0 and var s ? s : (int?)null;
        var (generation, inferred) = Generation(r.Int("SMBIOSMemoryType"), r.Int("MemoryType"), part, rated);
        return new MemoryModule
        {
            Slot = r.String("DeviceLocator"),
            BankLabel = r.String("BankLabel"),
            CapacityBytes = r.ULong("Capacity") ?? 0,
            RatedSpeed = rated,
            RunningSpeed = r.Int("ConfiguredClockSpeed") is > 0 and var c ? c : null,
            Manufacturer = Vendor(r.String("Manufacturer")),
            PartNumber = part,
            FormFactor = FormFactor(r.Int("FormFactor"), generation),
            Generation = generation,
            GenerationInferred = inferred,
        };
    }

    /// <summary>
    /// SMBIOS memory type to a generation: 24 DDR3, 26 DDR4, 34 DDR5, 29 LPDDR3, 30 LPDDR4, 35 LPDDR5 (plan.md 4.3).
    /// Falls back to the older MemoryType field, then the part number, then the speed.
    /// </summary>
    public static (string? Generation, bool Inferred) Generation(int? smbiosType, int? memoryType, string? partNumber, int? ratedSpeed)
    {
        var fromSmbios = FromSmbiosType(smbiosType) ?? FromSmbiosType(memoryType);
        if (fromSmbios is not null)
        {
            return (fromSmbios, false);
        }

        var fromPart = FromPartNumber(partNumber);
        if (fromPart is not null)
        {
            return (fromPart, true);
        }

        return ratedSpeed switch
        {
            >= 4800 => ("DDR5", true),
            >= 2133 and <= 3200 => ("DDR4", true),
            >= 800 and <= 1866 => ("DDR3", true),
            _ => (null, false),
        };
    }

    public static string? FromSmbiosType(int? code) => code switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => null,
    };

    /// <summary>Part numbers that name the generation ("...DDR4...") or use well-known vendor prefixes.</summary>
    public static string? FromPartNumber(string? part)
    {
        if (part is null)
        {
            return null;
        }

        var p = part.ToUpperInvariant();
        foreach (var gen in new[] { "LPDDR5", "LPDDR4", "LPDDR3", "DDR5", "DDR4", "DDR3" })
        {
            if (p.Contains(gen, StringComparison.Ordinal))
            {
                return gen;
            }
        }

        // Kingston: KF5/KVR5x = DDR5, KF4/KVR4x/KVR2x/KVR3x... too ambiguous below; only the clear ones.
        if (p.StartsWith("KF5", StringComparison.Ordinal) || p.StartsWith("KVR5", StringComparison.Ordinal))
        {
            return "DDR5";
        }

        // Samsung module part numbers: M4xxR = DDR5, M4xxA = DDR4, M4xxB = DDR3.
        if (p.Length > 5 && p[0] == 'M' && p[1] == '4')
        {
            return p[4] switch
            {
                'R' => "DDR5",
                'A' => "DDR4",
                'B' => "DDR3",
                _ => null,
            };
        }

        return null;
    }

    /// <summary>8 = DIMM, 12 = SODIMM; LPDDR is always soldered to the board.</summary>
    public static string? FormFactor(int? code, string? generation)
    {
        if (generation?.StartsWith("LPDDR", StringComparison.Ordinal) == true)
        {
            return "Soldered";
        }

        return code switch
        {
            8 => "DIMM",
            12 => "SODIMM",
            13 => "Soldered", // SRIMM/onboard on some firmware
            _ => null,
        };
    }

    private static readonly Dictionary<string, string> JedecVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["80CE"] = "Samsung",
        ["CE00"] = "Samsung",
        ["80AD"] = "SK hynix",
        ["AD00"] = "SK hynix",
        ["802C"] = "Micron",
        ["2C00"] = "Micron",
        ["0198"] = "Kingston",
        ["9801"] = "Kingston",
        ["859B"] = "Crucial",
        ["9B85"] = "Crucial",
        ["04CB"] = "A-DATA",
        ["CB04"] = "A-DATA",
        ["04CD"] = "G.Skill",
        ["CD04"] = "G.Skill",
        ["029E"] = "Corsair",
        ["9E02"] = "Corsair",
        ["8551"] = "Qimonda",
        ["0443"] = "Ramaxel",
        ["4304"] = "Ramaxel",
        ["8A45"] = "CXMT",
    };

    /// <summary>Turns JEDEC vendor codes ("80CE", "80CE000080CE") into names.</summary>
    public static string? Vendor(string? manufacturer)
    {
        if (manufacturer is null)
        {
            return null;
        }

        var key = manufacturer.Length >= 4 ? manufacturer[..4] : manufacturer;
        return JedecVendors.TryGetValue(key, out var name) && manufacturer.All(Uri.IsHexDigit) ? name : manufacturer;
    }
}

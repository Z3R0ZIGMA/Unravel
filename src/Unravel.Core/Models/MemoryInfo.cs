namespace Unravel.Core.Models;

/// <summary>One installed memory module (plan.md section 4.3).</summary>
public sealed record MemoryModule
{
    /// <summary>Slot name (DeviceLocator), e.g. "DIMM A1" or "Controller0-ChannelA".</summary>
    public string? Slot { get; init; }

    public string? BankLabel { get; init; }

    public ulong CapacityBytes { get; init; }

    /// <summary>Rated speed in MT/s (Win32_PhysicalMemory.Speed).</summary>
    public int? RatedSpeed { get; init; }

    /// <summary>Running speed in MT/s (ConfiguredClockSpeed).</summary>
    public int? RunningSpeed { get; init; }

    public string? Manufacturer { get; init; }

    public string? PartNumber { get; init; }

    /// <summary>"DIMM", "SODIMM", "Soldered" or null.</summary>
    public string? FormFactor { get; init; }

    /// <summary>"DDR4", "DDR5", "LPDDR5"... or null when unknown.</summary>
    public string? Generation { get; init; }

    /// <summary>True when <see cref="Generation"/> was guessed (part number or speed) rather than read from SMBIOS.</summary>
    public bool GenerationInferred { get; init; }
}

/// <summary>Memory section: modules plus upgrade information.</summary>
public sealed record MemoryInfo
{
    public IReadOnlyList<MemoryModule> Modules { get; init; } = [];

    /// <summary>Installed memory (sum of modules, or the OS figure when modules are not reported).</summary>
    public ulong InstalledBytes { get; init; }

    /// <summary>Memory Windows can use (Win32_ComputerSystem.TotalPhysicalMemory).</summary>
    public ulong? UsableBytes { get; init; }

    /// <summary>Total memory slots on the board, when the firmware reports it.</summary>
    public int? TotalSlots { get; init; }

    public int SlotsUsed => Modules.Count;

    public int? SlotsFree => TotalSlots is { } t && t >= SlotsUsed ? t - SlotsUsed : null;

    /// <summary>Largest total capacity the board supports (Win32_PhysicalMemoryArray.MaxCapacityEx).</summary>
    public ulong? MaxCapacityBytes { get; init; }

    /// <summary>Generation shared by the modules ("DDR5"), "Mixed", or null.</summary>
    public string? Generation
    {
        get
        {
            var gens = Modules.Select(m => m.Generation).Where(g => g is not null).Distinct().ToList();
            return gens.Count switch
            {
                0 => null,
                1 => gens[0],
                _ => "Mixed",
            };
        }
    }

    public bool GenerationInferred => Modules.Any(m => m.GenerationInferred);

    /// <summary>True when every module is soldered (LPDDR): no upgrade possible.</summary>
    public bool AllSoldered => Modules.Count > 0 && Modules.All(m => m.FormFactor == "Soldered");

    /// <summary>At least one module runs slower than it is rated for.</summary>
    public bool RunningBelowRated => Modules.Any(m => m.RatedSpeed is { } r && m.RunningSpeed is { } c && c > 0 && c < r);

    /// <summary>One module only, so memory runs single-channel (soldered LPDDR is multi-channel by design).</summary>
    public bool SingleChannel => Modules.Count == 1 && !AllSoldered;

    /// <summary>Modules of different sizes: part of the memory may run single-channel.</summary>
    public bool Mismatched => Modules.Count > 1 && Modules.Select(m => m.CapacityBytes).Distinct().Count() > 1;
}

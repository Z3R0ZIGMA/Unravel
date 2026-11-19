namespace Unravel.Core.Models;

/// <summary>One physical processor (socket). Most PCs have one.</summary>
public sealed record ProcessorInfo
{
    public string? Name { get; init; }

    public string? Manufacturer { get; init; }

    public int? Cores { get; init; }

    public int? LogicalProcessors { get; init; }

    /// <summary>Base clock in MHz (Win32_Processor.MaxClockSpeed).</summary>
    public int? BaseClockMhz { get; init; }

    public int? CurrentClockMhz { get; init; }

    public int? L2CacheKb { get; init; }

    public int? L3CacheKb { get; init; }

    public string? Socket { get; init; }

    /// <summary>"x64", "ARM64", "x86".</summary>
    public string? Architecture { get; init; }

    /// <summary>Virtualisation (VT-x / AMD-V) enabled in firmware. Null when Windows cannot tell (e.g. Hyper-V is running).</summary>
    public bool? VirtualizationEnabled { get; init; }

    /// <summary>"12th gen Intel Core", "Ryzen 7000 series"... parsed from the name; null when unknown.</summary>
    public string? Generation { get; init; }

    /// <summary>Intel Core generation number (8, 12...) or AMD Ryzen series (3000, 5000...), for Win11 readiness later.</summary>
    public int? GenerationNumber { get; init; }
}

/// <summary>Processor section (plan.md section 4.3).</summary>
public sealed record CpuInfo
{
    public IReadOnlyList<ProcessorInfo> Processors { get; init; } = [];

    public ProcessorInfo? Primary => Processors.Count > 0 ? Processors[0] : null;

    public int Sockets => Processors.Count;

    public int? TotalCores => Processors.All(p => p.Cores is null) ? null : Processors.Sum(p => p.Cores ?? 0);

    public int? TotalThreads => Processors.All(p => p.LogicalProcessors is null) ? null : Processors.Sum(p => p.LogicalProcessors ?? 0);
}

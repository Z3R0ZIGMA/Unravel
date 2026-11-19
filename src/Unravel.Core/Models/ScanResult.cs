using Unravel.Core.Collectors;

namespace Unravel.Core.Models;

/// <summary>All collector results from one scan. Sections that failed or have not finished are null.</summary>
public sealed record ScanResult
{
    public DateTimeOffset StartedAt { get; init; }

    public TimeSpan Duration { get; init; }

    public bool IsAdmin { get; init; }

    public IReadOnlyList<CollectorResult> Results { get; init; } = [];

    public DeviceInfo? Device => Find<DeviceInfo>();

    public CpuInfo? Cpu => Find<CpuInfo>();

    public MemoryInfo? Memory => Find<MemoryInfo>();

    public StorageInfo? Storage => Find<StorageInfo>();

    public OsInfo? Os => Find<OsInfo>();

    public IEnumerable<string> Warnings => Results.SelectMany(r => r.Warnings);

    public bool NeedsAdmin => Results.Any(r => r.NeedsAdmin);

    public CollectorResult? ResultFor(string section) =>
        Results.FirstOrDefault(r => string.Equals(r.Section, section, StringComparison.OrdinalIgnoreCase));

    private T? Find<T>() where T : class => Results.Select(r => r.Data).OfType<T>().FirstOrDefault();
}

public static class Sections
{
    public const string Device = "Device";
    public const string Cpu = "Processor";
    public const string Memory = "Memory";
    public const string Storage = "Storage";
    public const string Os = "Windows";
}

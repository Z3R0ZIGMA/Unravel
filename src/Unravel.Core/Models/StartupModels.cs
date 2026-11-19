namespace Unravel.Core.Models;

/// <summary>Something that starts with Windows or at sign-in (plan.md section 4.7).</summary>
public sealed record StartupItem
{
    public required string Name { get; init; }

    public string? Command { get; init; }

    /// <summary>"Registry (all users)", "Registry (this user)", "Startup folder (all users)", "Scheduled task"...</summary>
    public required string Location { get; init; }

    /// <summary>False when turned off in Task Manager / Settings > Apps > Startup (or a disabled task).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Company from the program's version information, when it could be read.</summary>
    public string? Publisher { get; init; }
}

public sealed record ServiceItem
{
    public required string Name { get; init; }

    public string? DisplayName { get; init; }

    public string? State { get; init; }

    /// <summary>"Automatic", "Automatic (delayed)", "Manual", "Disabled".</summary>
    public string? StartMode { get; init; }

    public string? Account { get; init; }

    public string? Path { get; init; }

    public string? Company { get; init; }

    public bool IsMicrosoft { get; init; }

    /// <summary>Starts only when something needs it (trigger start): not running is normal.</summary>
    public bool TriggerStart { get; init; }
}

public sealed record StartupServicesInfo
{
    public IReadOnlyList<StartupItem> StartupItems { get; init; } = [];

    /// <summary>Services not from Microsoft.</summary>
    public IReadOnlyList<ServiceItem> ThirdPartyServices { get; init; } = [];

    /// <summary>Services set to start automatically that are not running.</summary>
    public IReadOnlyList<ServiceItem> StoppedAutomatic { get; init; } = [];

    /// <summary>The stopped automatic services that are not trigger-start (the ones worth a look).</summary>
    public IEnumerable<ServiceItem> UnexpectedlyStopped => StoppedAutomatic.Where(s => !s.TriggerStart);
}

/// <summary>A device with a problem: the yellow exclamation mark in Device Manager.</summary>
public sealed record ProblemDevice(string Name, string? DeviceClass, int ErrorCode, string Problem, bool IsDisabled);

public sealed record DriverEntry(string Device, string? Provider, string? Version, DateTimeOffset? Date, string? DeviceClass);

public sealed record DriverInfo
{
    public IReadOnlyList<ProblemDevice> ProblemDevices { get; init; } = [];

    /// <summary>The oldest non-Microsoft drivers in use (null when the driver list could not be read).</summary>
    public IReadOnlyList<DriverEntry>? OldestThirdParty { get; init; }
}

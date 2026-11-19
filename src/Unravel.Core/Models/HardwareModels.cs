namespace Unravel.Core.Models;

// ---------------------------------------------------------------- graphics and displays (plan.md 4.3)

public sealed record GpuInfo
{
    public string? Name { get; init; }

    /// <summary>Dedicated video memory. Null for integrated GPUs that share system memory, or when unknown.</summary>
    public ulong? VramBytes { get; init; }

    public string? DriverVersion { get; init; }

    public DateTimeOffset? DriverDate { get; init; }

    /// <summary>"1920 × 1080 @ 60 Hz" for the output this GPU drives, when Windows reports it.</summary>
    public string? CurrentMode { get; init; }
}

public sealed record MonitorInfo
{
    /// <summary>Brand from the EDID manufacturer code ("DEL" to "Dell").</summary>
    public string? Make { get; init; }

    public string? Model { get; init; }

    public string? SerialNumber { get; init; }

    /// <summary>Diagonal in inches, from the EDID image size.</summary>
    public double? SizeInches { get; init; }

    public int? YearOfManufacture { get; init; }

    /// <summary>"Built-in", "HDMI", "DisplayPort", "USB-C / DisplayPort"...</summary>
    public string? Connection { get; init; }

    public bool IsInternal { get; init; }
}

public sealed record GraphicsInfo
{
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];

    public IReadOnlyList<MonitorInfo> Monitors { get; init; } = [];
}

// ---------------------------------------------------------------- motherboard and firmware (plan.md 4.3)

public sealed record TpmInfo
{
    public bool Present { get; init; }

    /// <summary>"2.0" or "1.2".</summary>
    public string? Version { get; init; }

    /// <summary>Only known when read as administrator.</summary>
    public bool? Enabled { get; init; }

    public bool? Activated { get; init; }

    public string? Manufacturer { get; init; }

    /// <summary>True when the details came from Win32_Tpm (admin); false when only the device list was read.</summary>
    public bool Detailed { get; init; }
}

public sealed record FirmwareInfo
{
    public string? BoardManufacturer { get; init; }

    public string? BoardModel { get; init; }

    public string? BoardVersion { get; init; }

    public string? BoardSerial { get; init; }

    public string? BiosVendor { get; init; }

    public string? BiosVersion { get; init; }

    public DateTimeOffset? BiosDate { get; init; }

    /// <summary>"UEFI" or "Legacy BIOS".</summary>
    public string? BootMode { get; init; }

    /// <summary>Null when Windows cannot tell (legacy BIOS boot, or the state is not reported).</summary>
    public bool? SecureBoot { get; init; }

    /// <summary>Null when the TPM could not be checked at all.</summary>
    public TpmInfo? Tpm { get; init; }

    public double? BiosAgeYears(DateTimeOffset now) => BiosDate is { } d ? (now - d).TotalDays / 365.25 : null;
}

// ---------------------------------------------------------------- network (plan.md 4.3)

public sealed record NetworkAdapterInfo
{
    public string? Name { get; init; }

    public string? Description { get; init; }

    /// <summary>"Ethernet", "Wi-Fi", "Bluetooth", "Mobile broadband", "Virtual", "Other".</summary>
    public required string Kind { get; init; }

    public string? MacAddress { get; init; }

    public bool Connected { get; init; }

    public ulong? LinkSpeedBps { get; init; }

    public IReadOnlyList<string> IPv4 { get; init; } = [];

    public IReadOnlyList<string> IPv6 { get; init; } = [];

    public IReadOnlyList<string> Gateways { get; init; } = [];

    public IReadOnlyList<string> DnsServers { get; init; } = [];

    public bool? Dhcp { get; init; }

    public bool IsVirtual { get; init; }
}

public sealed record WifiInfo
{
    public string? Ssid { get; init; }

    public int? SignalPercent { get; init; }

    public string? RadioType { get; init; }

    public string? Band { get; init; }

    public string? Channel { get; init; }

    public string? ReceiveRateMbps { get; init; }

    public string? TransmitRateMbps { get; init; }

    public string? State { get; init; }

    /// <summary>Why some details are missing (for example the Windows location permission hides the SSID).</summary>
    public string? Note { get; init; }
}

public sealed record NetworkInfo
{
    public IReadOnlyList<NetworkAdapterInfo> Adapters { get; init; } = [];

    public WifiInfo? Wifi { get; init; }
}

// ---------------------------------------------------------------- battery summary (plan.md 4.3)

public sealed record BatterySummary
{
    public string? Name { get; init; }

    public string? Manufacturer { get; init; }

    public long? DesignCapacityMWh { get; init; }

    public long? FullChargeCapacityMWh { get; init; }

    public int? CycleCount { get; init; }

    public int? ChargePercent { get; init; }

    /// <summary>Full-charge capacity as a percentage of the design capacity.</summary>
    public double? HealthPercent => DesignCapacityMWh is > 0 && FullChargeCapacityMWh is > 0
        ? 100.0 * FullChargeCapacityMWh.Value / DesignCapacityMWh.Value
        : null;
}

public sealed record BatteryInfo
{
    public IReadOnlyList<BatterySummary> Batteries { get; init; } = [];

    public bool HasBattery => Batteries.Count > 0;

    public BatterySummary? Primary => Batteries.Count > 0 ? Batteries[0] : null;
}

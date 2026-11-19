namespace Unravel.Core.Models;

public enum ChassisKind
{
    Unknown,
    Desktop,
    Laptop,
    MiniPc,
    AllInOne,
    Tablet,
    Convertible,
    Server,
    VirtualMachine,
    Other,
}

/// <summary>Device identity (plan.md section 4.2). Null strings mean "Not set".</summary>
public sealed record DeviceInfo
{
    /// <summary>Manufacturer as reported ("Dell Inc.", "LENOVO").</summary>
    public string? Manufacturer { get; init; }

    /// <summary>Short brand name ("Dell", "Lenovo", "ASUS").</summary>
    public string? Make { get; init; }

    /// <summary>Model as reported. On Lenovo this is the machine type ("21AHCTO1WW").</summary>
    public string? Model { get; init; }

    /// <summary>Friendly model name where the OEM puts it elsewhere (Lenovo: "ThinkPad T14 Gen 3").</summary>
    public string? FriendlyModel { get; init; }

    public string? SerialNumber { get; init; }

    public string? Sku { get; init; }

    public string? Uuid { get; init; }

    public ChassisKind Chassis { get; init; }

    /// <summary>The raw SMBIOS chassis type code, for reference.</summary>
    public int? ChassisCode { get; init; }

    public string? ComputerName { get; init; }

    /// <summary>The AD domain when joined, else null.</summary>
    public string? Domain { get; init; }

    public string? Workgroup { get; init; }

    public bool PartOfDomain { get; init; }

    public bool IsVirtualMachine => VmPlatform is not null;

    /// <summary>"Hyper-V", "VMware", "VirtualBox", "QEMU"... or null on real hardware.</summary>
    public string? VmPlatform { get; init; }

    /// <summary>"Lenovo ThinkPad T14 Gen 3", "Dell Latitude 5440", or the best we have.</summary>
    public string DisplayName
    {
        get
        {
            var model = FriendlyModel ?? Model;
            if (Make is null)
            {
                return model ?? "Unknown device";
            }

            if (model is null)
            {
                return $"{Make} (model not set)";
            }

            // Avoid "HP HP EliteBook" / "Microsoft Surface" duplication.
            return model.StartsWith(Make, StringComparison.OrdinalIgnoreCase) ? model : $"{Make} {model}";
        }
    }

    public string ChassisText => Chassis switch
    {
        ChassisKind.MiniPc => "Mini PC",
        ChassisKind.AllInOne => "All-in-one",
        ChassisKind.VirtualMachine => VmPlatform is null ? "Virtual machine" : $"Virtual machine ({VmPlatform})",
        ChassisKind.Convertible => "Convertible / 2-in-1",
        _ => Chassis.ToString(),
    };
}

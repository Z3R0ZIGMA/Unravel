using Unravel.Core.Infrastructure;
using Unravel.Core.Models;

namespace Unravel.Core.Collectors;

/// <summary>Make, model, serial, chassis, UUID, name and domain (plan.md section 4.2).</summary>
public sealed class DeviceCollector : CollectorBase<DeviceInfo>
{
    public override string Section => Sections.Device;

    protected override DeviceInfo Collect(CollectContext ctx, CollectorNotes notes, CancellationToken ct)
    {
        var cs = TryQuery(ctx, notes, "computer system", WmiNamespaces.Cimv2, "Win32_ComputerSystem",
            ["Manufacturer", "Model", "Name", "Domain", "PartOfDomain", "Workgroup", "SystemSKUNumber"]).FirstOrDefault();
        ct.ThrowIfCancellationRequested();
        var product = TryQuery(ctx, notes, "system product", WmiNamespaces.Cimv2, "Win32_ComputerSystemProduct",
            ["Name", "Version", "UUID", "Vendor"]).FirstOrDefault();
        var bios = TryQuery(ctx, notes, "BIOS serial number", WmiNamespaces.Cimv2, "Win32_BIOS", ["SerialNumber"]).FirstOrDefault();
        var enclosure = TryQuery(ctx, notes, "chassis", WmiNamespaces.Cimv2, "Win32_SystemEnclosure",
            ["SerialNumber", "ChassisTypes"]).FirstOrDefault();

        if (cs is null && product is null && bios is null)
        {
            throw new InvalidOperationException("WMI returned no device information");
        }

        var manufacturer = cs?.String("Manufacturer") ?? product?.String("Vendor");
        var make = DeviceRules.NormalizeMake(manufacturer);
        var model = cs?.String("Model") ?? product?.String("Name");
        var friendly = DeviceRules.FriendlyModel(make, model, product?.String("Version"));
        var vm = DeviceRules.DetectVm(manufacturer, model, product?.String("Version"));
        var chassisCode = enclosure?.LongArray("ChassisTypes").Select(c => (int)c).FirstOrDefault(c => c is not (1 or 2));
        var partOfDomain = cs?.Bool("PartOfDomain") ?? false;

        return new DeviceInfo
        {
            Manufacturer = manufacturer,
            Make = make,
            Model = model,
            FriendlyModel = friendly,
            SerialNumber = bios?.String("SerialNumber") ?? enclosure?.String("SerialNumber"),
            Sku = cs?.String("SystemSKUNumber"),
            Uuid = product?.String("UUID"),
            ChassisCode = chassisCode is null or 0 ? null : chassisCode,
            Chassis = vm is not null ? ChassisKind.VirtualMachine : DeviceRules.Chassis(chassisCode),
            VmPlatform = vm,
            ComputerName = cs?.RawString("Name") ?? Environment.MachineName,
            PartOfDomain = partOfDomain,
            Domain = partOfDomain ? cs?.RawString("Domain") : null,
            Workgroup = partOfDomain ? null : cs?.RawString("Workgroup") ?? cs?.RawString("Domain"),
        };
    }
}

/// <summary>Pure rules used by <see cref="DeviceCollector"/>, kept separate so they are easy to test.</summary>
public static class DeviceRules
{
    private static readonly (string Match, string Make)[] Makes =
    [
        ("Dell", "Dell"),
        ("Hewlett-Packard", "HP"),
        ("HP", "HP"),
        ("Lenovo", "Lenovo"),
        ("ASUSTeK", "ASUS"),
        ("ASUS", "ASUS"),
        ("Acer", "Acer"),
        ("Microsoft", "Microsoft"),
        ("Micro-Star", "MSI"),
        ("MSI", "MSI"),
        ("Gigabyte", "Gigabyte"),
        ("Samsung", "Samsung"),
        ("Toshiba", "Toshiba"),
        ("Dynabook", "Dynabook"),
        ("Fujitsu", "Fujitsu"),
        ("Panasonic", "Panasonic"),
        ("Intel", "Intel"),
        ("Apple", "Apple"),
        ("Razer", "Razer"),
        ("LG Electronics", "LG"),
        ("Huawei", "Huawei"),
        ("VMware", "VMware"),
        ("innotek", "Oracle VirtualBox"),
        ("QEMU", "QEMU"),
    ];

    /// <summary>"Dell Inc." to "Dell", "LENOVO" to "Lenovo", "ASUSTeK COMPUTER INC." to "ASUS".</summary>
    public static string? NormalizeMake(string? manufacturer)
    {
        if (manufacturer is null)
        {
            return null;
        }

        foreach (var (match, make) in Makes)
        {
            if (manufacturer.StartsWith(match, StringComparison.OrdinalIgnoreCase))
            {
                return make;
            }
        }

        // Strip legal suffixes from anything else: "Framework Computer Inc." to "Framework Computer".
        var cleaned = manufacturer;
        foreach (var suffix in new[] { " Corporation", " Corp.", " Inc.", " Inc", " Co., Ltd.", " Ltd.", " Limited", " GmbH", "," })
        {
            if (cleaned.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[..^suffix.Length].Trim();
            }
        }

        return cleaned.Length == 0 ? manufacturer : cleaned;
    }

    /// <summary>
    /// Lenovo puts the machine type in Model ("21AHCTO1WW") and the friendly name in Win32_ComputerSystemProduct.Version
    /// ("ThinkPad T14 Gen 3"). Returns the friendly name when it is better than the model, else null.
    /// </summary>
    public static string? FriendlyModel(string? make, string? model, string? productVersion)
    {
        if (productVersion is null || !string.Equals(make, "Lenovo", StringComparison.Ordinal))
        {
            return null;
        }

        // Version is sometimes just "Lenovo" or a BIOS-like string.
        if (productVersion.Equals("Lenovo", StringComparison.OrdinalIgnoreCase) || productVersion.Equals(model, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return productVersion.StartsWith("Lenovo ", StringComparison.OrdinalIgnoreCase) ? productVersion[7..].Trim() : productVersion;
    }

    /// <summary>Returns the hypervisor name when the manufacturer or model belongs to a virtual machine.</summary>
    public static string? DetectVm(string? manufacturer, string? model, string? productVersion)
    {
        var text = $"{manufacturer} | {model} | {productVersion}";
        bool Has(string s) => text.Contains(s, StringComparison.OrdinalIgnoreCase);

        if (Has("VMware"))
        {
            return "VMware";
        }

        if (Has("VirtualBox") || Has("innotek"))
        {
            return "VirtualBox";
        }

        if (Has("QEMU") || Has("KVM"))
        {
            return "QEMU/KVM";
        }

        if (Has("Parallels"))
        {
            return "Parallels";
        }

        if (Has("Xen"))
        {
            return "Xen";
        }

        // Hyper-V reports Microsoft Corporation + "Virtual Machine".
        if (Has("Microsoft") && Has("Virtual Machine"))
        {
            return "Hyper-V";
        }

        return null;
    }

    /// <summary>SMBIOS chassis type (Win32_SystemEnclosure.ChassisTypes) to a simple kind.</summary>
    public static ChassisKind Chassis(int? code) => code switch
    {
        3 or 4 or 5 or 6 or 7 or 15 or 16 or 24 => ChassisKind.Desktop,
        8 or 9 or 10 or 14 => ChassisKind.Laptop,
        11 => ChassisKind.Laptop,           // handheld
        13 => ChassisKind.AllInOne,
        17 or 23 or 25 or 28 or 29 => ChassisKind.Server,
        30 => ChassisKind.Tablet,
        31 or 32 => ChassisKind.Convertible,
        34 or 35 or 36 => ChassisKind.MiniPc, // embedded PC, mini PC, stick PC
        null or 0 or 1 or 2 => ChassisKind.Unknown,
        _ => ChassisKind.Other,
    };
}

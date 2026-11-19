using Unravel.Core.Infrastructure;
using Unravel.Core.Models;

namespace Unravel.Core.Collectors;

/// <summary>Devices with problems (Device Manager's yellow exclamation marks) and the oldest third-party drivers.</summary>
public sealed class DriverCollector : CollectorBase<DriverInfo>
{
    public override string Section => Sections.Drivers;

    protected override DriverInfo Collect(CollectContext ctx, CollectorNotes notes, CancellationToken ct)
    {
        var problems = TryQuery(ctx, notes, "device problems", WmiNamespaces.Cimv2, "Win32_PnPEntity",
                ["Name", "PNPClass", "ConfigManagerErrorCode", "DeviceID"], "ConfigManagerErrorCode <> 0")
            .Select(d => (Code: d.Int("ConfigManagerErrorCode") ?? 0, Row: d))
            // 45 = "not connected": a remembered device that is unplugged, not a problem.
            .Where(d => d.Code is not (0 or 45))
            .Select(d => new ProblemDevice(
                d.Row.String("Name") ?? d.Row.RawString("DeviceID") ?? "Unknown device",
                d.Row.String("PNPClass"),
                d.Code,
                DriverRules.Problem(d.Code),
                d.Code == 22))
            .OrderBy(d => d.IsDisabled)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        ct.ThrowIfCancellationRequested();

        // Win32_PnPSignedDriver takes a few seconds; it only adds the "oldest drivers" list, so failures stay quiet.
        var quiet = new CollectorNotes();
        var drivers = TryQuery(ctx, quiet, "drivers", WmiNamespaces.Cimv2, "Win32_PnPSignedDriver",
            ["DeviceName", "DriverProviderName", "DriverVersion", "DriverDate", "DeviceClass"]);
        var oldest = quiet.Warnings.Count > 0 ? null : drivers
            .Where(d => d.String("DeviceName") is not null && d.String("DriverProviderName") is { } p && !DriverRules.IsMicrosoft(p))
            .Select(d => new DriverEntry(d.String("DeviceName")!, d.String("DriverProviderName"), d.String("DriverVersion"), d.Date("DriverDate"),
                d.String("DeviceClass")))
            // 21 June 2006 is the placeholder date of Windows' generic (inbox-style) drivers, not a real age.
            .Where(d => d.Date is { Year: > 1990 } date && !(date.Year == 2006 && date.Month == 6 && date.Day == 21))
            .DistinctBy(d => (d.Device, d.Version))
            .OrderBy(d => d.Date)
            .Take(10)
            .ToList();

        return new DriverInfo { ProblemDevices = problems, OldestThirdParty = oldest };
    }
}

public static class DriverRules
{
    public static bool IsMicrosoft(string provider) =>
        provider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) || provider.Equals("Generic", StringComparison.OrdinalIgnoreCase);

    /// <summary>Device Manager problem codes (CM_PROB_*) to plain English.</summary>
    public static string Problem(int code) => code switch
    {
        1 => "Not configured correctly (code 1)",
        3 => "Driver may be corrupted or the system is low on memory (code 3)",
        10 => "Device cannot start (code 10)",
        12 => "Not enough free resources (code 12)",
        14 => "Needs a restart (code 14)",
        16 => "Cannot identify all resources (code 16)",
        18 => "Reinstall the drivers (code 18)",
        19 => "Registry configuration is damaged (code 19)",
        21 => "Windows is removing this device (code 21)",
        22 => "Disabled (code 22)",
        24 => "Not present or not working properly (code 24)",
        28 => "Drivers are not installed (code 28)",
        29 => "Disabled in the firmware (code 29)",
        31 => "Not working properly: Windows cannot load the driver (code 31)",
        32 => "Driver service is disabled (code 32)",
        37 => "Driver failed to initialise (code 37)",
        38 => "Previous driver instance still loaded (code 38)",
        39 => "Driver is corrupted or missing (code 39)",
        40 => "Service key information is missing (code 40)",
        41 => "Driver loaded but the hardware was not found (code 41)",
        43 => "Windows stopped this device after it reported problems (code 43)",
        47 => "Prepared for safe removal (code 47)",
        48 => "Driver blocked: known compatibility problem (code 48)",
        49 => "Registry too large (code 49)",
        52 => "Driver is not digitally signed (code 52)",
        _ => $"Problem code {code}",
    };
}

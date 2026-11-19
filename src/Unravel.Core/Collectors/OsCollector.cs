using System.Globalization;
using Unravel.Core.Infrastructure;
using Unravel.Core.Models;

namespace Unravel.Core.Collectors;

/// <summary>
/// Windows edition, version, build, install and boot dates, locale, activation and pending reboot (plan.md section 4.4).
/// Update history and the end-of-support check come in a later phase.
/// </summary>
public sealed class OsCollector : CollectorBase<OsInfo>
{
    public override string Section => Sections.Os;

    private const string CurrentVersion = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    /// <summary>The ApplicationID of Windows itself in SoftwareLicensingProduct (Office has its own).</summary>
    private const string WindowsAppId = "55c92734-d682-4d71-983e-d6ec3f16059f";

    protected override OsInfo Collect(CollectContext ctx, CollectorNotes notes, CancellationToken ct)
    {
        var os = TryQuery(ctx, notes, "Windows details", WmiNamespaces.Cimv2, "Win32_OperatingSystem",
            ["Caption", "BuildNumber", "OSArchitecture", "InstallDate", "LastBootUpTime", "Locale", "MUILanguages", "RegisteredUser"])
            .FirstOrDefault();
        var src = ctx.Source;

        var build = os?.Int("BuildNumber") ?? ParseInt(src.RegistryValue(CurrentVersion, "CurrentBuildNumber"));
        var caption = os?.String("Caption");
        if (caption is null && build is null)
        {
            throw new InvalidOperationException(notes.Warnings.LastOrDefault() ?? "WMI returned no operating system");
        }

        ct.ThrowIfCancellationRequested();
        var lastBoot = os?.Date("LastBootUpTime");
        var (activation, activated, channel) = ReadActivation(ctx, notes);

        return new OsInfo
        {
            Name = OsRules.Name(caption, build, src.RegistryValue(CurrentVersion, "ProductName") as string),
            EditionId = src.RegistryValue(CurrentVersion, "EditionID") as string,
            DisplayVersion = src.RegistryValue(CurrentVersion, "DisplayVersion") as string ?? src.RegistryValue(CurrentVersion, "ReleaseId") as string,
            Build = build,
            Ubr = ParseInt(src.RegistryValue(CurrentVersion, "UBR")),
            Architecture = OsRules.Architecture(
                src.RegistryValue(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "PROCESSOR_ARCHITECTURE") as string,
                os?.String("OSArchitecture")),
            InstallDate = os?.Date("InstallDate"),
            LastBoot = lastBoot,
            Uptime = lastBoot is { } boot && ctx.Now > boot ? ctx.Now - boot : null,
            Locale = OsRules.CultureName(os?.RawString("Locale")),
            Language = os?["MUILanguages"] is object[] langs ? string.Join(", ", langs.OfType<string>()) : null,
            TimeZone = src.RegistryValue(@"HKLM\SYSTEM\CurrentControlSet\Control\TimeZoneInformation", "TimeZoneKeyName") as string,
            ActivationStatus = activation,
            IsActivated = activated,
            LicenseChannel = channel,
            PendingRebootReasons = PendingRebootReasons(src),
            PendingFileRenames = src.RegistryValue(@"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations")
                is string[] { Length: > 0 } renames && renames.Any(r => !string.IsNullOrWhiteSpace(r)),
            RegisteredOwner = os?.String("RegisteredUser"),
        };
    }

    private static (string? Status, bool? Activated, string? Channel) ReadActivation(CollectContext ctx, CollectorNotes notes)
    {
        // Filtered on the Windows application and an installed key: an unfiltered query lists every product and is slow.
        var product = TryQuery(ctx, notes, "activation status", WmiNamespaces.Cimv2, "SoftwareLicensingProduct",
                ["Name", "Description", "LicenseStatus", "PartialProductKey"],
                $"ApplicationID = '{WindowsAppId}' AND PartialProductKey IS NOT NULL")
            .FirstOrDefault(p => p.RawString("PartialProductKey") is not null);
        if (product is null)
        {
            return (null, null, null);
        }

        var status = product.Int("LicenseStatus");
        return (OsRules.LicenseStatus(status), status is null ? null : status == 1, OsRules.LicenseChannel(product.RawString("Description")));
    }

    private static List<string> PendingRebootReasons(ISystemSource src)
    {
        var reasons = new List<string>();
        if (src.RegistryKeyExists(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
        {
            reasons.Add("Windows Update");
        }

        if (src.RegistryKeyExists(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
        {
            reasons.Add("Windows component servicing");
        }

        return reasons;
    }

    private static int? ParseInt(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
        _ => null,
    };
}

/// <summary>Pure OS rules.</summary>
public static class OsRules
{
    /// <summary>
    /// "Microsoft Windows 11 Pro" to "Windows 11 Pro". The registry ProductName still says "Windows 10" on Windows 11,
    /// so it is only a fallback, corrected by the build number (22000 and later is Windows 11).
    /// </summary>
    public static string? Name(string? caption, int? build, string? productName)
    {
        var name = caption ?? productName;
        if (name is null)
        {
            return build is null ? null : build >= 22000 ? "Windows 11" : "Windows 10";
        }

        if (name.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase))
        {
            name = name[10..];
        }

        if (build >= 22000 && name.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
        {
            name = name.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
        }

        return name.Trim();
    }

    /// <summary>PROCESSOR_ARCHITECTURE (AMD64 / ARM64 / x86), falling back to OSArchitecture ("64-bit").</summary>
    public static string? Architecture(string? processorArchitecture, string? osArchitecture) =>
        processorArchitecture?.ToUpperInvariant() switch
        {
            "AMD64" => "x64",
            "ARM64" => "ARM64",
            "X86" => "x86",
            _ => osArchitecture switch
            {
                null => null,
                var a when a.Contains("ARM", StringComparison.OrdinalIgnoreCase) => "ARM64",
                var a when a.Contains("64", StringComparison.Ordinal) => "x64",
                var a when a.Contains("32", StringComparison.Ordinal) => "x86",
                var a => a,
            },
        };

    /// <summary>Win32_OperatingSystem.Locale is a hex LCID ("0809") to "en-GB".</summary>
    public static string? CultureName(string? hexLcid)
    {
        if (hexLcid is null || !int.TryParse(hexLcid, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var lcid))
        {
            return null;
        }

        try
        {
            return CultureInfo.GetCultureInfo(lcid).Name;
        }
        catch (CultureNotFoundException)
        {
            return hexLcid;
        }
    }

    /// <summary>SoftwareLicensingProduct.LicenseStatus to text.</summary>
    public static string? LicenseStatus(int? status) => status switch
    {
        0 => "Not activated",
        1 => "Activated",
        2 => "Grace period (out of box)",
        3 => "Grace period (hardware changed)",
        4 => "Grace period (non-genuine)",
        5 => "Not activated (notification mode)",
        6 => "Grace period (extended)",
        _ => null,
    };

    /// <summary>"Windows(R) Operating System, OEM_DM channel" to "OEM".</summary>
    public static string? LicenseChannel(string? description)
    {
        if (description is null)
        {
            return null;
        }

        var d = description.ToUpperInvariant();
        if (d.Contains("VOLUME_KMSCLIENT", StringComparison.Ordinal))
        {
            return "Volume (KMS)";
        }

        if (d.Contains("VOLUME_MAK", StringComparison.Ordinal))
        {
            return "Volume (MAK)";
        }

        if (d.Contains("OEM", StringComparison.Ordinal))
        {
            return "OEM";
        }

        if (d.Contains("RETAIL", StringComparison.Ordinal))
        {
            return "Retail";
        }

        if (d.Contains("VOLUME", StringComparison.Ordinal))
        {
            return "Volume";
        }

        return null;
    }
}

using System.Text.RegularExpressions;
using Unravel.Core.Infrastructure;
using Unravel.Core.Models;

namespace Unravel.Core.Collectors;

/// <summary>Motherboard, BIOS, UEFI vs legacy boot, Secure Boot and TPM (plan.md section 4.3).</summary>
public sealed partial class FirmwareCollector : CollectorBase<FirmwareInfo>
{
    public override string Section => Sections.Firmware;

    protected override FirmwareInfo Collect(CollectContext ctx, CollectorNotes notes, CancellationToken ct)
    {
        var board = TryQuery(ctx, notes, "motherboard", WmiNamespaces.Cimv2, "Win32_BaseBoard",
            ["Manufacturer", "Product", "Version", "SerialNumber"]).FirstOrDefault();
        var bios = TryQuery(ctx, notes, "BIOS", WmiNamespaces.Cimv2, "Win32_BIOS",
            ["Manufacturer", "SMBIOSBIOSVersion", "ReleaseDate", "Version"]).FirstOrDefault();
        ct.ThrowIfCancellationRequested();
        if (board is null && bios is null)
        {
            throw new InvalidOperationException(notes.Warnings.LastOrDefault() ?? "WMI returned no firmware information");
        }

        // PEFirmwareType (1 = BIOS, 2 = UEFI) exists on some builds only. Otherwise the Secure Boot state value, which
        // Windows only writes on UEFI firmware, tells us it is UEFI.
        var secureBootValue = RegNumber(ctx, @"HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
        var bootMode = RegNumber(ctx, @"HKLM\SYSTEM\CurrentControlSet\Control", "PEFirmwareType") switch
        {
            2 => "UEFI",
            1 => "Legacy BIOS",
            _ => secureBootValue is not null ? "UEFI" : null,
        };
        var secureBoot = secureBootValue switch
        {
            1 => true,
            0 => false,
            _ => bootMode == "Legacy BIOS" ? false : (bool?)null,
        };

        return new FirmwareInfo
        {
            BoardManufacturer = board?.String("Manufacturer"),
            BoardModel = board?.String("Product"),
            BoardVersion = board?.String("Version"),
            BoardSerial = board?.String("SerialNumber"),
            BiosVendor = bios?.String("Manufacturer"),
            BiosVersion = bios?.String("SMBIOSBIOSVersion") ?? bios?.String("Version"),
            BiosDate = bios?.Date("ReleaseDate"),
            BootMode = bootMode,
            SecureBoot = secureBoot,
            Tpm = ReadTpm(ctx, notes),
        };
    }

    /// <summary>Win32_Tpm has the details but needs admin; the device list says whether a TPM (and which version) exists.</summary>
    private static TpmInfo? ReadTpm(CollectContext ctx, CollectorNotes notes)
    {
        if (ctx.IsAdmin)
        {
            try
            {
                var tpm = ctx.Source.Query(WmiNamespaces.Tpm, "Win32_Tpm",
                    ["IsEnabled_InitialValue", "IsActivated_InitialValue", "SpecVersion", "ManufacturerIdTxt"]).FirstOrDefault();
                if (tpm is null)
                {
                    return new TpmInfo { Present = false, Detailed = true };
                }

                return new TpmInfo
                {
                    Present = true,
                    Detailed = true,
                    Version = FirmwareRules.TpmVersion(tpm.RawString("SpecVersion")),
                    Enabled = tpm.Bool("IsEnabled_InitialValue"),
                    Activated = tpm.Bool("IsActivated_InitialValue"),
                    Manufacturer = tpm.String("ManufacturerIdTxt"),
                };
            }
            catch (SystemSourceException)
            {
                // Fall back to the device list below.
            }
        }

        var devices = TryQuery(ctx, notes, "TPM", WmiNamespaces.Cimv2, "Win32_PnPEntity", ["Name", "Status"], "Service = 'TPM'");
        if (!ctx.IsAdmin)
        {
            notes.NeedAdmin(); // TPM enabled/activated state needs Win32_Tpm.
        }

        var device = devices.FirstOrDefault();
        return device is null
            ? new TpmInfo { Present = false }
            : new TpmInfo { Present = true, Version = FirmwareRules.TpmVersion(device.String("Name")) };
    }
}

public static partial class FirmwareRules
{
    /// <summary>"2.0, 0, 1.59" (Win32_Tpm.SpecVersion) or "Trusted Platform Module 2.0" (device name) to "2.0".</summary>
    public static string? TpmVersion(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var m = Version().Match(text);
        return m.Success ? m.Value : null;
    }

    [GeneratedRegex(@"\b(1\.2|2\.0)\b")]
    private static partial Regex Version();
}

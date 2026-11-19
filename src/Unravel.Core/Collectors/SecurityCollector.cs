using Unravel.Core.Infrastructure;
using Unravel.Core.Models;

namespace Unravel.Core.Collectors;

/// <summary>Antivirus, Defender, firewall, BitLocker and UAC (plan.md section 4.6).</summary>
public sealed class SecurityCollector : CollectorBase<SecurityInfo>
{
    public override string Section => Sections.Security;

    protected override SecurityInfo Collect(CollectContext ctx, CollectorNotes notes, CancellationToken ct)
    {
        // Security Center does not exist on Windows Server: no warning for that, Defender status still shows.
        var quiet = new CollectorNotes();
        var antivirus = TryQuery(ctx, quiet, "antivirus products", WmiNamespaces.SecurityCenter2, "AntiVirusProduct",
                ["displayName", "productState"])
            .Where(a => a.String("displayName") is not null)
            .Select(a =>
            {
                var state = a.Long("productState");
                return new AntivirusProduct(a.String("displayName")!, SecurityRules.AvEnabled(state), SecurityRules.AvUpToDate(state));
            })
            .DistinctBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ct.ThrowIfCancellationRequested();

        var defender = TryQuery(ctx, notes, "Microsoft Defender status", WmiNamespaces.Defender, "MSFT_MpComputerStatus",
        [
            "AntivirusEnabled", "RealTimeProtectionEnabled", "IsTamperProtected", "AMRunningMode",
            "AntivirusSignatureLastUpdated", "AntivirusSignatureAge", "AntivirusSignatureVersion",
        ]).FirstOrDefault();

        return new SecurityInfo
        {
            Antivirus = antivirus,
            Defender = defender is null ? null : new DefenderStatus
            {
                AntivirusEnabled = defender.Bool("AntivirusEnabled"),
                RealTimeProtection = defender.Bool("RealTimeProtectionEnabled"),
                TamperProtection = defender.Bool("IsTamperProtected"),
                Mode = SecurityRules.DefenderMode(defender.RawString("AMRunningMode")),
                SignaturesUpdated = defender.Date("AntivirusSignatureLastUpdated"),
                SignatureAgeDays = defender.Int("AntivirusSignatureAge") is >= 0 and < 65535 and var age ? age : null,
                SignatureVersion = defender.RawString("AntivirusSignatureVersion"),
            },
            Firewall = ReadFirewall(ctx),
            BitLocker = ReadBitLocker(ctx, notes),
            UacEnabled = RegNumber(ctx, UacKey, "EnableLUA") is { } lua ? lua != 0 : null,
            UacLevel = SecurityRules.UacLevel(RegNumber(ctx, UacKey, "EnableLUA"), RegNumber(ctx, UacKey, "ConsentPromptBehaviorAdmin"),
                RegNumber(ctx, UacKey, "PromptOnSecureDesktop")),
        };
    }

    private const string UacKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    /// <summary>Firewall profile state, with Group Policy taking precedence over the local setting.</summary>
    private static List<FirewallProfile> ReadFirewall(CollectContext ctx)
    {
        const string local = @"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";
        const string policy = @"HKLM\SOFTWARE\Policies\Microsoft\WindowsFirewall";
        return
        [
            .. new[] { ("Domain", "DomainProfile"), ("Private", "StandardProfile"), ("Public", "PublicProfile") }.Select(p =>
            {
                var byPolicy = RegNumber(ctx, $@"{policy}\{p.Item2}", "EnableFirewall");
                var setting = byPolicy ?? RegNumber(ctx, $@"{local}\{p.Item2}", "EnableFirewall");
                return new FirewallProfile(p.Item1, setting is null ? null : setting != 0, byPolicy is not null);
            }),
        ];
    }

    private static List<BitLockerVolume>? ReadBitLocker(CollectContext ctx, CollectorNotes notes)
    {
        if (!ctx.IsAdmin)
        {
            notes.NeedAdmin();
            return null;
        }

        var systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").ToUpperInvariant();
        var volumes = TryQuery(ctx, notes, "BitLocker", WmiNamespaces.BitLocker, "Win32_EncryptableVolume",
            ["DriveLetter", "ProtectionStatus", "ConversionStatus", "EncryptionMethod", "VolumeType"]);
        return volumes
            .Where(v => v.RawString("DriveLetter") is not null)
            .Select(v => new BitLockerVolume
            {
                Volume = v.RawString("DriveLetter")!,
                ProtectionOn = v.Int("ProtectionStatus") switch
                {
                    1 => true,
                    0 => false,
                    _ => null,
                },
                Status = SecurityRules.ConversionStatus(v.Int("ConversionStatus")),
                Method = SecurityRules.EncryptionMethod(v.Int("EncryptionMethod")),
                IsSystemVolume = string.Equals(v.RawString("DriveLetter"), systemDrive, StringComparison.OrdinalIgnoreCase),
            })
            .OrderBy(v => v.Volume, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public static class SecurityRules
{
    /// <summary>Security Center productState: bits 12-15 = scanner state (1 = on).</summary>
    public static bool? AvEnabled(long? state) => state is { } s ? ((s >> 12) & 0xF) == 1 : null;

    /// <summary>Security Center productState: bits 4-7 = signature state (0 = up to date).</summary>
    public static bool? AvUpToDate(long? state) => state is { } s ? ((s >> 4) & 0xF) == 0 : null;

    public static string? DefenderMode(string? mode) => mode switch
    {
        null => null,
        "Normal" => "Normal",
        "Passive Mode" or "Passive" => "Passive",
        "EDR Block Mode" => "EDR block mode",
        "SxS Passive Mode" => "Passive (side by side)",
        "Not running" => "Off",
        var m => m,
    };

    public static string? UacLevel(long? enableLua, long? consentAdmin, long? secureDesktop)
    {
        if (enableLua == 0)
        {
            return "Off (UAC disabled)";
        }

        return (consentAdmin, secureDesktop) switch
        {
            (2, 1 or null) => "Always notify",
            (5 or null, 1 or null) => "Notify when apps make changes (default)",
            (5, 0) => "Notify when apps make changes, without dimming",
            (0, _) => "Never notify",
            (1 or 3, _) => "Ask for credentials",
            (4, _) => "Ask for consent",
            _ => null,
        };
    }

    public static string? ConversionStatus(int? code) => code switch
    {
        0 => "Fully decrypted",
        1 => "Fully encrypted",
        2 => "Encrypting",
        3 => "Decrypting",
        4 => "Encryption paused",
        5 => "Decryption paused",
        _ => null,
    };

    public static string? EncryptionMethod(int? code) => code switch
    {
        0 => null,
        1 => "AES-128 with diffuser",
        2 => "AES-256 with diffuser",
        3 => "AES-128",
        4 => "AES-256",
        5 => "Hardware encryption",
        6 => "XTS-AES-128",
        7 => "XTS-AES-256",
        _ => null,
    };
}

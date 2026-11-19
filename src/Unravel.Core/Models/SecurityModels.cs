namespace Unravel.Core.Models;

// ---------------------------------------------------------------- security (plan.md 4.6)

public sealed record AntivirusProduct(string Name, bool? Enabled, bool? UpToDate)
{
    public bool IsDefender => Name.Contains("Defender", StringComparison.OrdinalIgnoreCase);
}

public sealed record DefenderStatus
{
    public bool? AntivirusEnabled { get; init; }

    public bool? RealTimeProtection { get; init; }

    public bool? TamperProtection { get; init; }

    /// <summary>"Normal", "Passive" (another antivirus is primary), "EDR Block"...</summary>
    public string? Mode { get; init; }

    public DateTimeOffset? SignaturesUpdated { get; init; }

    public int? SignatureAgeDays { get; init; }

    public string? SignatureVersion { get; init; }
}

public sealed record FirewallProfile(string Name, bool? Enabled, bool SetByPolicy);

public sealed record BitLockerVolume
{
    public required string Volume { get; init; }

    /// <summary>True when protection is on (the volume is encrypted and the key is protected).</summary>
    public bool? ProtectionOn { get; init; }

    /// <summary>"Fully encrypted", "Fully decrypted", "Encrypting"...</summary>
    public string? Status { get; init; }

    public string? Method { get; init; }

    public bool IsSystemVolume { get; init; }
}

public sealed record SecurityInfo
{
    /// <summary>Products registered with Windows Security Center (not available on Windows Server).</summary>
    public IReadOnlyList<AntivirusProduct> Antivirus { get; init; } = [];

    public DefenderStatus? Defender { get; init; }

    public IReadOnlyList<FirewallProfile> Firewall { get; init; } = [];

    /// <summary>Null when BitLocker could not be read (it needs administrator rights).</summary>
    public IReadOnlyList<BitLockerVolume>? BitLocker { get; init; }

    /// <summary>"Default (notify when apps make changes)", "Always notify", "Off"...</summary>
    public string? UacLevel { get; init; }

    public bool? UacEnabled { get; init; }

    public bool HasActiveAntivirus => Antivirus.Any(a => a.Enabled == true)
                                      || (Defender?.RealTimeProtection == true && Defender.Mode is null or "Normal");

    public IEnumerable<AntivirusProduct> ThirdPartyAntivirus => Antivirus.Where(a => !a.IsDefender);
}

// ---------------------------------------------------------------- identity / management (plan.md 4.6)

public sealed record LocalAdmin(string Name, string Kind);

public sealed record IdentityInfo
{
    /// <summary>"Entra joined", "Hybrid Entra joined", "Domain joined", "Workgroup".</summary>
    public required string JoinType { get; init; }

    public bool EntraJoined { get; init; }

    public bool DomainJoined { get; init; }

    public bool WorkplaceJoined { get; init; }

    public string? DomainName { get; init; }

    public string? TenantName { get; init; }

    public string? TenantId { get; init; }

    public string? DeviceId { get; init; }

    /// <summary>"Intune", another MDM's name, or null when not enrolled.</summary>
    public string? Mdm { get; init; }

    public string? MdmUser { get; init; }

    /// <summary>The signed-in user has an Entra Primary Refresh Token (single sign-on works).</summary>
    public bool? HasPrt { get; init; }

    /// <summary>Null when the group could not be read.</summary>
    public IReadOnlyList<LocalAdmin>? LocalAdmins { get; init; }

    /// <summary>False when dsregcmd could not run and the join type came from WMI only.</summary>
    public bool FromDsregcmd { get; init; }
}

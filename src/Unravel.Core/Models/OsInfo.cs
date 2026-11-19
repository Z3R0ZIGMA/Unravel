namespace Unravel.Core.Models;

/// <summary>Operating system section (plan.md section 4.4, Phase 1 subset).</summary>
public sealed record OsInfo
{
    /// <summary>"Windows 11 Pro".</summary>
    public string? Name { get; init; }

    /// <summary>EditionID from the registry ("Professional", "Enterprise", "Core").</summary>
    public string? EditionId { get; init; }

    /// <summary>"24H2" (DisplayVersion; older builds use ReleaseId, e.g. "2009").</summary>
    public string? DisplayVersion { get; init; }

    /// <summary>Build number, e.g. 26100.</summary>
    public int? Build { get; init; }

    /// <summary>Update build revision, e.g. 2314.</summary>
    public int? Ubr { get; init; }

    /// <summary>"26100.2314".</summary>
    public string? FullBuild => Build is null ? null : Ubr is null ? $"{Build}" : $"{Build}.{Ubr}";

    /// <summary>"x64", "ARM64", "x86".</summary>
    public string? Architecture { get; init; }

    public DateTimeOffset? InstallDate { get; init; }

    public DateTimeOffset? LastBoot { get; init; }

    /// <summary>Time since the last boot (with Fast Startup, shutting down does not reset this).</summary>
    public TimeSpan? Uptime { get; init; }

    /// <summary>"en-GB".</summary>
    public string? Locale { get; init; }

    /// <summary>Display language(s) installed, e.g. "en-US".</summary>
    public string? Language { get; init; }

    /// <summary>"GMT Standard Time".</summary>
    public string? TimeZone { get; init; }

    /// <summary>"Licensed", "Not activated", "Grace period"... null when it could not be read.</summary>
    public string? ActivationStatus { get; init; }

    public bool? IsActivated { get; init; }

    /// <summary>"Retail", "OEM", "Volume (KMS)", "Volume (MAK)"...</summary>
    public string? LicenseChannel { get; init; }

    public bool PendingReboot => PendingRebootReasons.Count > 0;

    /// <summary>Why a reboot is pending ("Windows Update", "Windows component servicing").</summary>
    public IReadOnlyList<string> PendingRebootReasons { get; init; } = [];

    /// <summary>
    /// Files are queued to be replaced at the next restart (PendingFileRenameOperations). Many installers leave this
    /// set, so it is shown as information and does not count as a pending reboot on its own.
    /// </summary>
    public bool PendingFileRenames { get; init; }

    public string? RegisteredOwner { get; init; }
}

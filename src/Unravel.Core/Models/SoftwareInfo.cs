namespace Unravel.Core.Models;

/// <summary>One installed program (plan.md section 4.5).</summary>
public sealed record SoftwareEntry
{
    public required string Name { get; init; }

    public string? Version { get; init; }

    public string? Publisher { get; init; }

    public DateOnly? InstallDate { get; init; }

    public ulong? SizeBytes { get; init; }

    /// <summary>"64-bit", "32-bit" or the Store package architecture.</summary>
    public string? Architecture { get; init; }

    /// <summary>"Machine" (all users) or "User" (this user only).</summary>
    public required string Scope { get; init; }

    /// <summary>"Installed" (uninstall registry) or "Store".</summary>
    public required string Source { get; init; }

    public string? UninstallString { get; init; }

    /// <summary>System components, updates and patches: hidden by default, like Programs and Features does.</summary>
    public bool IsHidden { get; init; }

    /// <summary>Store framework/system packages (VCLibs, .NET Native, inbox apps): hidden by default.</summary>
    public bool IsSystemPackage { get; init; }

    /// <summary>Remote-access tool name ("AnyDesk") when this entry is one.</summary>
    public string? RemoteAccessTool { get; init; }
}

/// <summary>Microsoft 365 Apps / Click-to-Run Office (plan.md section 4.5 highlights).</summary>
public sealed record OfficeInfo
{
    public string? Products { get; init; }

    public string? Version { get; init; }

    /// <summary>"Current Channel", "Monthly Enterprise Channel", "Semi-Annual Enterprise Channel"...</summary>
    public string? Channel { get; init; }

    /// <summary>"64-bit" or "32-bit".</summary>
    public string? Bitness { get; init; }

    /// <summary>The Microsoft 365 account signed in to Office for this user, when readable.</summary>
    public string? Account { get; init; }
}

/// <summary>Several versions of the same product installed side by side.</summary>
public sealed record DuplicateGroup(string Name, IReadOnlyList<string> Versions);

public sealed record SoftwareInfo
{
    /// <summary>Everything found, hidden entries included (filter on <see cref="SoftwareEntry.IsHidden"/>).</summary>
    public IReadOnlyList<SoftwareEntry> Programs { get; init; } = [];

    public IReadOnlyList<SoftwareEntry> StoreApps { get; init; } = [];

    public OfficeInfo? Office { get; init; }

    public IReadOnlyList<DuplicateGroup> Duplicates { get; init; } = [];

    public IEnumerable<SoftwareEntry> Visible => Programs.Where(p => !p.IsHidden);

    public IEnumerable<SoftwareEntry> VisibleStoreApps => StoreApps.Where(a => !a.IsSystemPackage);

    public IEnumerable<SoftwareEntry> RemoteAccess =>
        Programs.Concat(StoreApps).Where(p => p.RemoteAccessTool is not null && !p.IsHidden);
}

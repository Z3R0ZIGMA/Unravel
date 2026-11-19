namespace Unravel.Core.Infrastructure;

/// <summary>
/// Where collectors read the machine from. <see cref="LiveSystemSource"/> reads WMI, the registry, tools and Windows
/// APIs; <see cref="FixtureSystemSource"/> replays data captured from a real machine, so parsing can be tested anywhere.
/// </summary>
public interface ISystemSource
{
    /// <summary>
    /// Reads the given properties of a WMI class. Throws <see cref="SystemSourceException"/> when the class
    /// cannot be read (missing namespace, access denied, timeout).
    /// </summary>
    IReadOnlyList<WmiRecord> Query(string wmiNamespace, string className, IReadOnlyList<string> properties, string? where = null);

    /// <summary>A registry value from the 64-bit view, or null. <paramref name="key"/> starts with HKLM\ or HKCU\.</summary>
    object? RegistryValue(string key, string name);

    /// <summary>True when the registry key exists (64-bit view).</summary>
    bool RegistryKeyExists(string key);

    /// <summary>The names of the key's direct subkeys (64-bit view); empty when the key is missing.</summary>
    IReadOnlyList<string> RegistrySubKeys(string key);

    /// <summary>All values under a key (64-bit view); empty when the key is missing.</summary>
    IReadOnlyDictionary<string, object?> RegistryValues(string key);

    /// <summary>
    /// Runs a built-in Windows tool (dsregcmd, netsh, net) with no window and returns its output, or null when it
    /// could not run or did not finish in time.
    /// </summary>
    string? RunCommand(string file, string arguments);

    /// <summary>Other live data as records. See <see cref="RowKinds"/> for what exists.</summary>
    IReadOnlyList<WmiRecord> Rows(string kind, string? argument = null);
}

/// <summary>The kinds of <see cref="ISystemSource.Rows"/>.</summary>
public static class RowKinds
{
    /// <summary>Store/AppX packages for the current user (Windows.Management.Deployment.PackageManager).</summary>
    public const string StoreApps = "StoreApps";

    /// <summary>Non-Microsoft scheduled tasks that run at logon or at startup.</summary>
    public const string StartupTasks = "StartupTasks";

    /// <summary>Files in a folder (argument: the folder path). Fields: Name, Path.</summary>
    public const string FolderFiles = "FolderFiles";

    /// <summary>Version resource of a file (argument: the file path). Fields: CompanyName, ProductName, FileVersion, FileDescription.</summary>
    public const string FileVersion = "FileVersion";

    /// <summary>`powercfg /batteryreport /xml` (works without admin). Field: Xml.</summary>
    public const string BatteryReport = "BatteryReport";

    /// <summary>Members of a local group (argument: the group SID). Fields: Name, Kind.</summary>
    public const string LocalGroupMembers = "LocalGroupMembers";
}

public static class WmiNamespaces
{
    public const string Cimv2 = @"root\cimv2";
    public const string Storage = @"root\Microsoft\Windows\Storage";
    public const string Wmi = @"root\wmi";
    public const string StandardCimv2 = @"root\StandardCimv2";
    public const string SecurityCenter2 = @"root\SecurityCenter2";
    public const string Defender = @"root\Microsoft\Windows\Defender";
    public const string Tpm = @"root\cimv2\Security\MicrosoftTpm";
    public const string BitLocker = @"root\cimv2\Security\MicrosoftVolumeEncryption";
}

/// <summary>A WMI or registry read failed. <see cref="AccessDenied"/> means it may work as administrator.</summary>
public sealed class SystemSourceException(string message, bool accessDenied = false, Exception? inner = null)
    : Exception(message, inner)
{
    public bool AccessDenied { get; } = accessDenied;
}

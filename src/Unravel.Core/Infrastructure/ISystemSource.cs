namespace Unravel.Core.Infrastructure;

/// <summary>
/// Where collectors read the machine from. <see cref="LiveSystemSource"/> reads WMI and the registry;
/// <see cref="FixtureSystemSource"/> replays data captured from a real machine, so parsing can be tested anywhere.
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
}

public static class WmiNamespaces
{
    public const string Cimv2 = @"root\cimv2";
    public const string Storage = @"root\Microsoft\Windows\Storage";
}

/// <summary>A WMI or registry read failed. <see cref="AccessDenied"/> means it may work as administrator.</summary>
public sealed class SystemSourceException(string message, bool accessDenied = false, Exception? inner = null)
    : Exception(message, inner)
{
    public bool AccessDenied { get; } = accessDenied;
}

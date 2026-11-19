using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Unravel.Core.Infrastructure;

/// <summary>Reads this machine: WMI through <see cref="Wmi"/>, registry through the 64-bit view, tools, Windows APIs.</summary>
public sealed class LiveSystemSource : ISystemSource
{
    private readonly string? _userSid;
    private readonly bool _noUser;

    /// <summary>Reads as the current user: HKCU and Store apps are this account's.</summary>
    public LiveSystemSource()
    {
    }

    private LiveSystemSource(string? userSid)
    {
        _userSid = userSid;
        _noUser = userSid is null;
    }

    /// <summary>
    /// For the system account (RMM): HKCU and Store apps are read for <paramref name="userSid"/>, the person signed in at
    /// the screen; with nobody signed in they are empty rather than the system account's own.
    /// </summary>
    public static LiveSystemSource ForUser(string? userSid) => new(userSid);

    public IReadOnlyList<WmiRecord> Query(string wmiNamespace, string className, IReadOnlyList<string> properties, string? where = null)
    {
        try
        {
            return Wmi.Query(wmiNamespace, className, properties, where);
        }
        catch (ManagementException ex)
        {
            var denied = ex.ErrorCode == ManagementStatus.AccessDenied;
            var reason = ex.ErrorCode switch
            {
                ManagementStatus.AccessDenied => "access denied",
                ManagementStatus.InvalidNamespace => "not available on this Windows version",
                ManagementStatus.InvalidClass => "not available on this Windows version",
                ManagementStatus.NotSupported => "not supported on this PC",
                ManagementStatus.Timedout => "WMI did not answer in time",
                _ => ex.Message.Trim(),
            };
            throw new SystemSourceException($"{className}: {reason}", denied, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SystemSourceException($"{className}: access denied", true, ex);
        }
        catch (COMException ex)
        {
            // 0x80041003 = WBEM_E_ACCESS_DENIED surfaced as a COM error by some providers.
            var denied = (uint)ex.HResult is 0x80041003 or 0x80070005;
            throw new SystemSourceException(denied ? $"{className}: access denied" : $"{className}: WMI error 0x{ex.HResult:X8}", denied, ex);
        }
    }

    public object? RegistryValue(string key, string name)
    {
        try
        {
            using var k = Open(key);
            return k?.GetValue(name);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public bool RegistryKeyExists(string key)
    {
        try
        {
            using var k = Open(key);
            return k is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public IReadOnlyList<string> RegistrySubKeys(string key)
    {
        try
        {
            using var k = Open(key);
            return k?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    public IReadOnlyDictionary<string, object?> RegistryValues(string key)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var k = Open(key);
            if (k is null)
            {
                return values;
            }

            foreach (var name in k.GetValueNames())
            {
                values[name] = k.GetValue(name, null, RegistryValueOptions.None);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Return what was read.
        }

        return values;
    }

    public string? RunCommand(string file, string arguments) => ProcessRunner.Run(file, arguments);

    public IReadOnlyList<WmiRecord> Rows(string kind, string? argument = null) => kind switch
    {
        RowKinds.StoreApps when _noUser => [],
        RowKinds.StoreApps => LiveRows.StoreApps(_userSid),
        _ => LiveRows.Read(kind, argument),
    };

    private RegistryKey? Open(string key)
    {
        var (hive, path) = Split(key);
        if (hive == RegistryHive.CurrentUser && (_userSid is not null || _noUser))
        {
            if (_noUser)
            {
                return null;
            }

            // The signed-in user's hive, loaded under HKEY_USERS\<SID> while they are signed in.
            hive = RegistryHive.Users;
            path = path.Length == 0 ? _userSid! : $@"{_userSid}\{path}";
        }

        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        return root.OpenSubKey(path, writable: false);
    }

    internal static (RegistryHive Hive, string Path) Split(string key)
    {
        var slash = key.IndexOf('\\', StringComparison.Ordinal);
        var hiveName = slash < 0 ? key : key[..slash];
        var path = slash < 0 ? "" : key[(slash + 1)..];
        var hive = hiveName.ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKU" or "HKEY_USERS" => RegistryHive.Users,
            _ => throw new ArgumentException($"Unknown registry hive in '{key}'.", nameof(key)),
        };
        return (hive, path);
    }
}

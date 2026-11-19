using System.Management;

namespace Unravel.Core.Infrastructure;

/// <summary>
/// WMI query helper with a timeout. Every <see cref="ManagementObjectSearcher"/>, collection and object is disposed
/// (WMI objects leak otherwise); values are copied into <see cref="WmiRecord"/>s first.
/// Never used with Win32_Product (slow, and it triggers MSI self-repair).
/// </summary>
public static class Wmi
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public static IReadOnlyList<WmiRecord> Query(
        string wmiNamespace, string className, IReadOnlyList<string> properties, string? where = null, TimeSpan? timeout = null)
    {
        if (string.Equals(className, "Win32_Product", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Win32_Product must never be queried: it triggers MSI self-repair. Use the registry.");
        }

        var select = properties.Count == 0 ? "*" : string.Join(", ", properties);
        try
        {
            return Run(wmiNamespace, Wql(select, className, where), properties, timeout);
        }
        catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.InvalidQuery or ManagementStatus.InvalidProperty
                                                 && properties.Count > 0)
        {
            // An older Windows build or an OEM's implementation lacks one of the properties: ask for everything
            // and keep what exists, rather than losing the whole class.
            return Run(wmiNamespace, Wql("*", className, where), properties, timeout);
        }
    }

    private static string Wql(string select, string className, string? where) =>
        where is null ? $"SELECT {select} FROM {className}" : $"SELECT {select} FROM {className} WHERE {where}";

    private static List<WmiRecord> Run(string wmiNamespace, string wql, IReadOnlyList<string> wanted, TimeSpan? timeout)
    {
        var options = new System.Management.EnumerationOptions
        {
            Timeout = timeout ?? DefaultTimeout,
            ReturnImmediately = true,
            Rewindable = false,
        };

        using var searcher = new ManagementObjectSearcher(new ManagementScope(wmiNamespace), new ObjectQuery(wql), options);
        using var results = searcher.Get();

        var list = new List<WmiRecord>();
        foreach (var obj in results)
        {
            using (obj)
            {
                list.Add(Copy(obj, wanted));
            }
        }

        return list;
    }

    private static WmiRecord Copy(ManagementBaseObject obj, IReadOnlyList<string> wanted)
    {
        var values = new List<KeyValuePair<string, object?>>();
        foreach (var property in obj.Properties)
        {
            if (wanted.Count > 0 && !wanted.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // Arrays are copied so nothing keeps a reference into the COM object.
            var value = property.Value is Array array ? array.Cast<object?>().ToArray() : property.Value;
            values.Add(new(property.Name, value));
        }

        return new WmiRecord(values);
    }
}

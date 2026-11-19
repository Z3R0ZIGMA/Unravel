using System.Collections;
using System.Globalization;
using System.Management;

namespace Unravel.Core.Infrastructure;

/// <summary>
/// One WMI result copied into plain .NET values, so the <see cref="ManagementObject"/> it came from can be
/// disposed straight away. The same type is rebuilt from JSON fixtures in tests.
/// </summary>
public sealed class WmiRecord
{
    private readonly Dictionary<string, object?> _values;

    public WmiRecord(IEnumerable<KeyValuePair<string, object?>> values)
    {
        _values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            _values[key] = value;
        }
    }

    public IReadOnlyDictionary<string, object?> Values => _values;

    public object? this[string name] => _values.TryGetValue(name, out var v) ? v : null;

    /// <summary>Trimmed string with OEM placeholders removed (null when not set).</summary>
    public string? String(string name) => OemValues.Clean(RawString(name));

    /// <summary>Trimmed string, placeholders kept (for values where "0" or "None" are real answers).</summary>
    public string? RawString(string name)
    {
        var value = this[name];
        if (value is null)
        {
            return null;
        }

        var s = Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace("\0", "", StringComparison.Ordinal).Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    public long? Long(string name) => Number(name, v => Convert.ToInt64(v, CultureInfo.InvariantCulture));

    public ulong? ULong(string name) => Number(name, v => Convert.ToUInt64(v, CultureInfo.InvariantCulture));

    public int? Int(string name) => Number(name, v => Convert.ToInt32(v, CultureInfo.InvariantCulture));

    public bool? Bool(string name) => this[name] switch
    {
        bool b => b,
        string s when bool.TryParse(s, out var b) => b,
        null => null,
        var v => Number(name, x => Convert.ToInt64(x, CultureInfo.InvariantCulture)) is { } n ? n != 0 : null,
    };

    /// <summary>A WMI array property (for example <c>ChassisTypes</c>) as numbers.</summary>
    public IReadOnlyList<long> LongArray(string name)
    {
        if (this[name] is not IEnumerable items || this[name] is string)
        {
            return Long(name) is { } single ? [single] : [];
        }

        var list = new List<long>();
        foreach (var item in items)
        {
            try
            {
                list.Add(Convert.ToInt64(item, CultureInfo.InvariantCulture));
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                // Skip values that are not numbers.
            }
        }

        return list;
    }

    /// <summary>A CIM datetime ("20241105093012.500000+060") as a local time.</summary>
    public DateTimeOffset? Date(string name)
    {
        var s = RawString(name);
        if (s is null)
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(s));
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException or FormatException)
        {
            return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null;
        }
    }

    private T? Number<T>(string name, Func<object, T> convert) where T : struct
    {
        var value = this[name];
        if (value is null || value is string { Length: 0 })
        {
            return null;
        }

        try
        {
            return convert(value);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }
}

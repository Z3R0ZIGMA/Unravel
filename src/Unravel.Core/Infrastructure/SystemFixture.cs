using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Unravel.Core.Infrastructure;

/// <summary>
/// Everything the collectors read from one machine (WMI results and registry values), saved as JSON.
/// Captured with <c>Unravel.exe --capture-fixture file.json</c>; replayed by <see cref="FixtureSystemSource"/>.
/// </summary>
public sealed class SystemFixture
{
    public string? Description { get; set; }

    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>Key: "namespace:Class", value: one dictionary per instance.</summary>
    public Dictionary<string, List<Dictionary<string, object?>>> Wmi { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>WMI classes that failed when captured, with the reason ("access denied"...).</summary>
    public Dictionary<string, string> WmiErrors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Key: registry key path; value: the values read under it. A present key with no values still exists.</summary>
    public Dictionary<string, Dictionary<string, object?>> Registry { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    internal static string WmiKey(string wmiNamespace, string className) => $"{wmiNamespace}:{className}";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static SystemFixture Load(string path) =>
        JsonSerializer.Deserialize<SystemFixture>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"{path} is not a system fixture.");

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
}

/// <summary>Replays a <see cref="SystemFixture"/>. Classes that were not captured count as "not available".</summary>
public sealed class FixtureSystemSource(SystemFixture fixture) : ISystemSource
{
    public IReadOnlyList<WmiRecord> Query(string wmiNamespace, string className, IReadOnlyList<string> properties, string? where = null)
    {
        var key = SystemFixture.WmiKey(wmiNamespace, className);
        if (fixture.WmiErrors.TryGetValue(key, out var error))
        {
            throw new SystemSourceException($"{className}: {error}", error.Contains("denied", StringComparison.OrdinalIgnoreCase));
        }

        if (!fixture.Wmi.TryGetValue(key, out var rows))
        {
            throw new SystemSourceException($"{className}: not available on this Windows version");
        }

        return rows.Select(r => new WmiRecord(r.Select(kv => new KeyValuePair<string, object?>(kv.Key, FromJson(kv.Value))))).ToList();
    }

    public object? RegistryValue(string key, string name) =>
        fixture.Registry.TryGetValue(key, out var values) && values.TryGetValue(name, out var v) ? FromJson(v) : null;

    public bool RegistryKeyExists(string key) => fixture.Registry.ContainsKey(key);

    /// <summary>Turns <see cref="JsonElement"/>s back into plain values (string, long, double, bool, object[]).</summary>
    private static object? FromJson(object? value) => value switch
    {
        JsonElement e => e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.TryGetUInt64(out var u) ? u : e.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => e.EnumerateArray().Select(x => FromJson(x)).ToArray(),
            _ => null,
        },
        _ => value,
    };
}

/// <summary>Wraps a live source and records everything read, to save as a fixture.</summary>
public sealed class RecordingSystemSource(ISystemSource inner) : ISystemSource
{
    private readonly ConcurrentDictionary<string, List<Dictionary<string, object?>>> _wmi = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _wmiErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, object?>> _registry = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<WmiRecord> Query(string wmiNamespace, string className, IReadOnlyList<string> properties, string? where = null)
    {
        var key = SystemFixture.WmiKey(wmiNamespace, className);
        try
        {
            var rows = inner.Query(wmiNamespace, className, properties, where);
            _wmi[key] = rows.Select(r => r.Values.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)).ToList();
            return rows;
        }
        catch (SystemSourceException ex)
        {
            _wmiErrors[key] = ex.AccessDenied ? "access denied" : ex.Message;
            throw;
        }
    }

    public object? RegistryValue(string key, string name)
    {
        var value = inner.RegistryValue(key, name);
        if (value is not null || inner.RegistryKeyExists(key))
        {
            var values = _registry.GetOrAdd(key, _ => new(StringComparer.OrdinalIgnoreCase));
            if (value is not null)
            {
                values[name] = value;
            }
        }

        return value;
    }

    public bool RegistryKeyExists(string key)
    {
        var exists = inner.RegistryKeyExists(key);
        if (exists)
        {
            _registry.GetOrAdd(key, _ => new(StringComparer.OrdinalIgnoreCase));
        }

        return exists;
    }

    public SystemFixture ToFixture(string? description) => new()
    {
        Description = description,
        CapturedAt = DateTimeOffset.Now,
        Wmi = new(_wmi, StringComparer.OrdinalIgnoreCase),
        WmiErrors = new(_wmiErrors, StringComparer.OrdinalIgnoreCase),
        Registry = _registry.ToDictionary(kv => kv.Key, kv => new Dictionary<string, object?>(kv.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase),
    };
}

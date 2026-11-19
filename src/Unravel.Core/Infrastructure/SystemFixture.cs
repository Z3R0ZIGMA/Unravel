using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Unravel.Core.Infrastructure;

/// <summary>
/// Everything the collectors read from one machine (WMI, registry, tool output and other live data), saved as JSON.
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

    /// <summary>
    /// Key: registry key path; value: the values read under it. A key with no values still exists, and a key's
    /// subkeys are the other keys directly below it.
    /// </summary>
    public Dictionary<string, Dictionary<string, object?>> Registry { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Key: "tool arguments", value: its output (null when it failed).</summary>
    public Dictionary<string, string?> Commands { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Key: "kind" or "kind|argument" (see <see cref="RowKinds"/>).</summary>
    public Dictionary<string, List<Dictionary<string, object?>>> Rows { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Row kinds that failed when captured, with the reason.</summary>
    public Dictionary<string, string> RowErrors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"namespace:Class", plus "|where" when the query was filtered (the same class can be read with different filters).</summary>
    internal static string WmiKey(string wmiNamespace, string className, string? where = null) =>
        where is null ? $"{wmiNamespace}:{className}" : $"{wmiNamespace}:{className}|{where}";

    internal static string CommandKey(string file, string arguments) => $"{file} {arguments}".Trim();

    internal static string RowKey(string kind, string? argument) => argument is null ? kind : $"{kind}|{argument}";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static SystemFixture Load(string path)
    {
        var fixture = JsonSerializer.Deserialize<SystemFixture>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"{path} is not a system fixture.");

        // The deserializer builds plain dictionaries; lookups must ignore case like Windows does.
        fixture.Wmi = new(fixture.Wmi, StringComparer.OrdinalIgnoreCase);
        fixture.WmiErrors = new(fixture.WmiErrors, StringComparer.OrdinalIgnoreCase);
        fixture.Registry = fixture.Registry.ToDictionary(kv => kv.Key, kv => new Dictionary<string, object?>(kv.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        fixture.Commands = new(fixture.Commands ?? [], StringComparer.OrdinalIgnoreCase);
        fixture.Rows = new(fixture.Rows ?? [], StringComparer.OrdinalIgnoreCase);
        fixture.RowErrors = new(fixture.RowErrors ?? [], StringComparer.OrdinalIgnoreCase);
        return fixture;
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
}

/// <summary>Replays a <see cref="SystemFixture"/>. Anything that was not captured counts as "not available".</summary>
public sealed class FixtureSystemSource(SystemFixture fixture) : ISystemSource
{
    public IReadOnlyList<WmiRecord> Query(string wmiNamespace, string className, IReadOnlyList<string> properties, string? where = null)
    {
        // Filtered queries are stored under their filter; older fixtures stored them without it.
        var key = SystemFixture.WmiKey(wmiNamespace, className, where);
        if (where is not null && !fixture.Wmi.ContainsKey(key) && !fixture.WmiErrors.ContainsKey(key))
        {
            key = SystemFixture.WmiKey(wmiNamespace, className);
        }

        if (fixture.WmiErrors.TryGetValue(key, out var error))
        {
            throw new SystemSourceException($"{className}: {error}", error.Contains("denied", StringComparison.OrdinalIgnoreCase));
        }

        if (!fixture.Wmi.TryGetValue(key, out var rows))
        {
            throw new SystemSourceException($"{className}: not available on this Windows version");
        }

        return rows.Select(ToRecord).ToList();
    }

    public object? RegistryValue(string key, string name) =>
        fixture.Registry.TryGetValue(key, out var values) && values.TryGetValue(name, out var v) ? FromJson(v) : null;

    public bool RegistryKeyExists(string key) => fixture.Registry.ContainsKey(key);

    public IReadOnlyList<string> RegistrySubKeys(string key)
    {
        var prefix = key.TrimEnd('\\') + "\\";
        return fixture.Registry.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..].Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyDictionary<string, object?> RegistryValues(string key) =>
        fixture.Registry.TryGetValue(key, out var values)
            ? values.ToDictionary(kv => kv.Key, kv => FromJson(kv.Value), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>();

    public string? RunCommand(string file, string arguments) =>
        fixture.Commands.TryGetValue(SystemFixture.CommandKey(file, arguments), out var output) ? output : null;

    public IReadOnlyList<WmiRecord> Rows(string kind, string? argument = null)
    {
        var key = SystemFixture.RowKey(kind, argument);
        if (fixture.RowErrors.TryGetValue(key, out var error))
        {
            throw new SystemSourceException($"{kind}: {error}", error.Contains("denied", StringComparison.OrdinalIgnoreCase));
        }

        return fixture.Rows.TryGetValue(key, out var rows) ? rows.Select(ToRecord).ToList() : [];
    }

    private static WmiRecord ToRecord(Dictionary<string, object?> row) =>
        new(row.Select(kv => new KeyValuePair<string, object?>(kv.Key, FromJson(kv.Value))));

    /// <summary>Turns <see cref="JsonElement"/>s back into plain values (string, long, double, bool, object[]).</summary>
    private static object? FromJson(object? value) => value switch
    {
        JsonElement e => e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            // Boxed separately: a plain ternary would turn every number into a double.
            JsonValueKind.Number => e.TryGetInt64(out var l) ? (object)l : e.TryGetUInt64(out var u) ? u : e.GetDouble(),
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
    private readonly ConcurrentDictionary<string, string?> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<Dictionary<string, object?>>> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _rowErrors = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<WmiRecord> Query(string wmiNamespace, string className, IReadOnlyList<string> properties, string? where = null)
    {
        var key = SystemFixture.WmiKey(wmiNamespace, className, where);
        try
        {
            var rows = inner.Query(wmiNamespace, className, properties, where);
            var copies = rows.Select(Copy).ToList();
            // Several collectors read different properties of the same class (Win32_ComputerSystem): merge them per instance.
            _wmi.AddOrUpdate(key, copies, (_, existing) =>
            {
                lock (existing)
                {
                    for (var i = 0; i < copies.Count; i++)
                    {
                        if (i < existing.Count)
                        {
                            foreach (var (name, value) in copies[i])
                            {
                                existing[i][name] = value;
                            }
                        }
                        else
                        {
                            existing.Add(copies[i]);
                        }
                    }

                    return existing;
                }
            });
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
            var values = Key(key);
            if (value is not null)
            {
                values[name] = Plain(value);
            }
        }

        return value;
    }

    public bool RegistryKeyExists(string key)
    {
        var exists = inner.RegistryKeyExists(key);
        if (exists)
        {
            Key(key);
        }

        return exists;
    }

    public IReadOnlyList<string> RegistrySubKeys(string key)
    {
        var subKeys = inner.RegistrySubKeys(key);
        foreach (var sub in subKeys)
        {
            Key($"{key.TrimEnd('\\')}\\{sub}");
        }

        return subKeys;
    }

    public IReadOnlyDictionary<string, object?> RegistryValues(string key)
    {
        var values = inner.RegistryValues(key);
        if (values.Count > 0 || inner.RegistryKeyExists(key))
        {
            var recorded = Key(key);
            foreach (var (name, value) in values)
            {
                recorded[name] = Plain(value);
            }
        }

        return values;
    }

    public string? RunCommand(string file, string arguments)
    {
        var output = inner.RunCommand(file, arguments);
        _commands[SystemFixture.CommandKey(file, arguments)] = output;
        return output;
    }

    public IReadOnlyList<WmiRecord> Rows(string kind, string? argument = null)
    {
        var key = SystemFixture.RowKey(kind, argument);
        try
        {
            var rows = inner.Rows(kind, argument);
            _rows[key] = rows.Select(Copy).ToList();
            return rows;
        }
        catch (SystemSourceException ex)
        {
            _rowErrors[key] = ex.AccessDenied ? "access denied" : ex.Message;
            throw;
        }
    }

    private ConcurrentDictionary<string, object?> Key(string key) => _registry.GetOrAdd(key, _ => new(StringComparer.OrdinalIgnoreCase));

    private static Dictionary<string, object?> Copy(WmiRecord r) =>
        r.Values.ToDictionary(kv => kv.Key, kv => Plain(kv.Value), StringComparer.OrdinalIgnoreCase);

    /// <summary>Registry binary values become arrays of numbers (JSON would otherwise write base64).</summary>
    private static object? Plain(object? value) => value is byte[] bytes ? bytes.Select(b => (object)(int)b).ToArray() : value;

    public SystemFixture ToFixture(string? description) => new()
    {
        Description = description,
        CapturedAt = DateTimeOffset.Now,
        Wmi = new(_wmi, StringComparer.OrdinalIgnoreCase),
        WmiErrors = new(_wmiErrors, StringComparer.OrdinalIgnoreCase),
        Registry = _registry.ToDictionary(kv => kv.Key, kv => new Dictionary<string, object?>(kv.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase),
        Commands = new(_commands, StringComparer.OrdinalIgnoreCase),
        Rows = new(_rows, StringComparer.OrdinalIgnoreCase),
        RowErrors = new(_rowErrors, StringComparer.OrdinalIgnoreCase),
    };
}

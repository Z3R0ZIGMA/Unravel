using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Unravel.Core.Collectors;
using Unravel.Core.Models;

namespace Unravel.Core.Reports;

/// <summary>
/// "Redact sensitive fields" (plan.md section 8): a copy of the scan with serial numbers, names, addresses and IDs
/// replaced by "[redacted]", for reports that go to a third party.
/// <para>
/// Each section is copied through JSON: the sensitive properties are replaced by name, and every other text has
/// the same values (computer name, domain, account names), user folders, SIDs, MAC and e-mail addresses removed,
/// so they do not slip through in a path, an event message or a warning.
/// </para>
/// </summary>
public sealed partial class Redactor
{
    public const string Mark = "[redacted]";

    /// <summary>What the redaction hides, as shown on the Reports page and in the report.</summary>
    public const string Description =
        "Serial numbers and UUID, computer, user and account names, domain and tenant, device IDs, MAC and IP addresses, DNS servers and Wi-Fi network name.";

    /// <summary>Properties replaced whole (a string, or each string in a list).</summary>
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SerialNumber", "BoardSerial", "Uuid", "ComputerName", "RegisteredOwner", "Domain", "DomainName",
        "TenantName", "TenantId", "DeviceId", "MdmUser", "MacAddress", "IPv4", "IPv6", "Gateways", "DnsServers", "Ssid",
    };

    private static readonly JsonSerializerOptions CopyOptions = new() { Converters = { new JsonStringEnumConverter() } };

    private readonly List<string> _values;

    private Redactor(IEnumerable<string> values) =>
        _values = values.Where(v => v.Trim().Length >= 3).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(v => v.Length).ToList();

    /// <summary>A redactor that also removes this scan's own names wherever they appear in other text.</summary>
    public static Redactor For(ScanResult scan)
    {
        var values = new List<string?>
        {
            scan.Device?.ComputerName, scan.Device is { PartOfDomain: true } d ? d.Domain : null, scan.Device?.SerialNumber, scan.Device?.Uuid,
            scan.Firmware?.BoardSerial, scan.Os?.RegisteredOwner,
            scan.Identity?.DomainName, scan.Identity?.TenantName, scan.Identity?.TenantId, scan.Identity?.DeviceId, scan.Identity?.MdmUser,
            scan.Software?.Office?.Account,
        };

        // Local administrators: "DESKTOP-1AB2C3D\jane" and "AzureAD\JaneDoe" give "jane" and "JaneDoe".
        foreach (var admin in scan.Identity?.LocalAdmins ?? [])
        {
            if (IsPersonalAccount(admin.Name))
            {
                values.Add(admin.Name);
                values.Add(admin.Name[(admin.Name.LastIndexOf('\\') + 1)..]);
            }
        }

        return new Redactor(values.OfType<string>().Where(v => !string.Equals(v, Mark, StringComparison.Ordinal)));
    }

    public ScanResult Redact(ScanResult scan) => scan with { Results = scan.Results.Select(Redact).ToList() };

    public CollectorResult Redact(CollectorResult result) => result with
    {
        Data = result.Data is { } data ? Copy(data) : null,
        Warnings = result.Warnings.Select(Text).ToList(),
    };

    public ActiveTestRecord Redact(ActiveTestRecord test) => test with
    {
        Outcome = test.Outcome with { Summary = Text(test.Outcome.Summary), Details = test.Outcome.Details.Select(Text).ToList() },
    };

    /// <summary>Removes the known values and the patterns (user folders, SIDs, MAC and e-mail addresses) from free text.</summary>
    public string Text(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        foreach (var value in _values)
        {
            // Whole words only: a computer named "PC1" must not turn "PC10" into "[redacted]0".
            text = Regex.Replace(text, $@"(?<![\w-]){Regex.Escape(value)}(?![\w-])", Mark, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        text = UserFolder().Replace(text, Mark);
        text = Sid().Replace(text, "S-1-5-21-" + Mark);
        text = MacAddress().Replace(text, Mark);
        return Email().Replace(text, Mark);
    }

    private object Copy(object data)
    {
        var type = data.GetType();
        var node = JsonSerializer.SerializeToNode(data, type, CopyOptions);
        Walk(node);
        return node.Deserialize(type, CopyOptions) ?? data;
    }

    private void Walk(JsonNode? node, string? parent = null)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj.ToList())
                {
                    if (child is null)
                    {
                        continue;
                    }

                    if (IsSensitive(name, parent, child))
                    {
                        obj[name] = child switch
                        {
                            JsonArray array => new JsonArray(array.Select(_ => (JsonNode?)JsonValue.Create(Mark)).ToArray()),
                            JsonValue v when v.GetValueKind() == JsonValueKind.String => JsonValue.Create(Mark),
                            _ => child.DeepClone(),
                        };
                    }
                    else if (child is JsonValue value && value.GetValueKind() == JsonValueKind.String)
                    {
                        obj[name] = JsonValue.Create(Text(value.GetValue<string>()));
                    }
                    else
                    {
                        Walk(child, name);
                    }
                }

                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.GetValueKind() == JsonValueKind.String)
                    {
                        array[i] = JsonValue.Create(Text(value.GetValue<string>()));
                    }
                    else
                    {
                        Walk(array[i], parent);
                    }
                }

                break;
        }
    }

    private static bool IsSensitive(string name, string? parent, JsonNode value)
    {
        if (SensitiveNames.Contains(name))
        {
            return true;
        }

        // A local administrator's name, the Office account, a service's sign-in account.
        if (string.Equals(name, "Name", StringComparison.OrdinalIgnoreCase) && string.Equals(parent, "LocalAdmins", StringComparison.OrdinalIgnoreCase))
        {
            return value is JsonValue v && v.GetValueKind() == JsonValueKind.String && IsPersonalAccount(v.GetValue<string>());
        }

        return string.Equals(name, "Account", StringComparison.OrdinalIgnoreCase)
               && value is JsonValue a && a.GetValueKind() == JsonValueKind.String && IsPersonalAccount(a.GetValue<string>());
    }

    /// <summary>A person's (or a domain's) account, not a built-in one like LocalSystem or "NT AUTHORITY\NetworkService".</summary>
    private static bool IsPersonalAccount(string account)
    {
        if (account.Contains('@', StringComparison.Ordinal))
        {
            return true;
        }

        if (!account.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        // "DESKTOP-1AB2C3D\Administrator" names nobody: the computer or domain part is removed from the text instead.
        if (WellKnownAccounts.Contains(account[(account.LastIndexOf('\\') + 1)..]))
        {
            return false;
        }

        return !account.StartsWith("NT AUTHORITY\\", StringComparison.OrdinalIgnoreCase)
               && !account.StartsWith("NT SERVICE\\", StringComparison.OrdinalIgnoreCase)
               && !account.StartsWith("BUILTIN\\", StringComparison.OrdinalIgnoreCase)
               && !account.StartsWith("Window Manager\\", StringComparison.OrdinalIgnoreCase)
               && !account.StartsWith("Font Driver Host\\", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> WellKnownAccounts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Administrator", "Administrators", "Domain Admins", "Enterprise Admins", "Guest", "DefaultAccount", "WDAGUtilityAccount",
    };

    /// <summary>The folder name after "\Users\" (not Public or Default).</summary>
    [GeneratedRegex(@"(?<=\\Users\\)(?!Public\\|Default\\|Default User\\|All Users\\|\[redacted\])[^\\/""<>|:*?\r\n]+", RegexOptions.IgnoreCase)]
    private static partial Regex UserFolder();

    [GeneratedRegex(@"S-1-5-21-\d+-\d+-\d+(-\d+)?")]
    private static partial Regex Sid();

    [GeneratedRegex(@"\b([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b")]
    private static partial Regex MacAddress();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(\.[\w-]+)+")]
    private static partial Regex Email();
}

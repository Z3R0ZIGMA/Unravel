namespace Unravel.Core.Infrastructure;

/// <summary>
/// Cleans values that OEMs leave blank or fill with placeholders ("To be filled by O.E.M.", "Default string",
/// "System Serial Number"...). Such values are treated as "Not set" (null) everywhere in Unravel.
/// </summary>
public static class OemValues
{
    public const string NotSet = "Not set";

    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "To be filled by O.E.M.",
        "To be filled by OEM",
        "Default string",
        "Default",
        "System Serial Number",
        "System Product Name",
        "System manufacturer",
        "System Manufacturer",
        "System Version",
        "System SKU",
        "SKU",
        "Not Applicable",
        "Not Specified",
        "Not Available",
        "None",
        "N/A",
        "NA",
        "Unknown",
        "Undefined",
        "OEM",
        "O.E.M.",
        "Base Board Serial Number",
        "Chassis Serial Number",
        "Type1ProductConfigId",
        "Invalid",
        "XXXXXXXXXX",
        "0",
        "00000000",
        "000000000000",
        "0123456789",
        "1234567890",
        "123456789",
        "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF",
        "00000000-0000-0000-0000-000000000000",
        "03000200-0400-0500-0006-000700080009", // a common placeholder UUID on white-box boards
    };

    /// <summary>Trims the value and returns null for empty values and OEM placeholders.</summary>
    public static string? Clean(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Replace("\0", "", StringComparison.Ordinal).Trim();
        if (trimmed.Length == 0 || IsPlaceholder(trimmed))
        {
            return null;
        }

        return trimmed;
    }

    public static bool IsPlaceholder(string value)
    {
        var v = value.Trim();
        if (Placeholders.Contains(v))
        {
            return true;
        }

        // Runs of one repeated character ("0000000", "........", "FFFFFFFF") are never real values.
        var compact = v.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        return compact.Length >= 4 && compact.All(c => c == compact[0]) && (compact[0] is '0' or 'F' or 'f' or '.' or 'X' or 'x' or ' ');
    }

    /// <summary>The value, or "Not set" for display.</summary>
    public static string OrNotSet(string? value) => string.IsNullOrWhiteSpace(value) ? NotSet : value;
}

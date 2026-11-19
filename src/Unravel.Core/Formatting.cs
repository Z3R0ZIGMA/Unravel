using System.Globalization;

namespace Unravel.Core;

/// <summary>Display formatting shared by the app and (later) the reports.</summary>
public static class Formatting
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    /// <summary>Disk sizes in decimal units, as drive makers and Explorer's labels use: "512 GB", "1 TB".</summary>
    public static string DiskSize(ulong? bytes)
    {
        if (bytes is not { } b)
        {
            return "?";
        }

        const double gb = 1_000_000_000d;
        var value = b / gb;
        if (value >= 1000)
        {
            return $"{(value / 1000).ToString(value >= 10_000 ? "0" : "0.#", Culture)} TB";
        }

        return value >= 100 ? $"{value.ToString("0", Culture)} GB" : $"{value.ToString("0.#", Culture)} GB";
    }

    /// <summary>
    /// Volume sizes and free space in binary units, matching what Explorer shows for a drive ("118 GB free").
    /// </summary>
    public static string VolumeSize(ulong bytes)
    {
        const double gib = 1024d * 1024 * 1024;
        var value = bytes / gib;
        if (value >= 1024)
        {
            return $"{(value / 1024).ToString("0.##", Culture)} TB";
        }

        return value >= 100 ? $"{value.ToString("0", Culture)} GB" : $"{value.ToString("0.#", Culture)} GB";
    }

    /// <summary>Memory in binary GB, rounded: 17179869184 to "16 GB", 7.8 GiB usable to "7.8 GB".</summary>
    public static string Memory(ulong? bytes, bool exact = false)
    {
        if (bytes is not { } b)
        {
            return "?";
        }

        var gib = b / (1024d * 1024 * 1024);
        if (gib < 1)
        {
            return $"{(b / (1024d * 1024)).ToString("0", Culture)} MB";
        }

        return exact || Math.Abs(gib - Math.Round(gib)) > 0.05 ? $"{gib.ToString("0.#", Culture)} GB" : $"{Math.Round(gib).ToString("0", Culture)} GB";
    }

    public static string Clock(int? mhz) => mhz is { } m ? m >= 1000 ? $"{(m / 1000d).ToString("0.0#", Culture)} GHz" : $"{m} MHz" : "?";

    public static string Cache(int? kb) => kb is { } k ? k >= 1024 ? $"{(k / 1024d).ToString("0.#", Culture)} MB" : $"{k} KB" : "?";

    /// <summary>"3 days 4 h", "5 h 12 min", "12 min".</summary>
    public static string Duration(TimeSpan? span)
    {
        if (span is not { } s)
        {
            return "?";
        }

        if (s.TotalDays >= 1)
        {
            var days = (int)s.TotalDays;
            return $"{days} {(days == 1 ? "day" : "days")} {s.Hours} h";
        }

        if (s.TotalHours >= 1)
        {
            return $"{(int)s.TotalHours} h {s.Minutes} min";
        }

        return $"{Math.Max(0, s.Minutes)} min";
    }

    public static string Date(DateTimeOffset? date) => date is { } d ? d.ToLocalTime().ToString("d MMM yyyy", Culture) : "?";

    public static string DateTime(DateTimeOffset? date) => date is { } d ? d.ToLocalTime().ToString("d MMM yyyy HH:mm", Culture) : "?";

    public static string Percent(double value) => $"{value.ToString("0", Culture)}%";
}

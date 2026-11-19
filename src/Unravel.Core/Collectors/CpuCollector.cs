using System.Text.RegularExpressions;
using Unravel.Core.Infrastructure;
using Unravel.Core.Models;

namespace Unravel.Core.Collectors;

/// <summary>Processor details from Win32_Processor (plan.md section 4.3).</summary>
public sealed class CpuCollector : CollectorBase<CpuInfo>
{
    public override string Section => Sections.Cpu;

    protected override CpuInfo Collect(CollectContext ctx, CollectorNotes notes, CancellationToken ct)
    {
        var rows = TryQuery(ctx, notes, "processor", WmiNamespaces.Cimv2, "Win32_Processor",
        [
            "Name", "Manufacturer", "NumberOfCores", "NumberOfLogicalProcessors", "MaxClockSpeed", "CurrentClockSpeed",
            "L2CacheSize", "L3CacheSize", "SocketDesignation", "Architecture", "VirtualizationFirmwareEnabled",
        ]);
        if (rows.Count == 0)
        {
            throw new InvalidOperationException(notes.Warnings.LastOrDefault() ?? "WMI returned no processor");
        }

        var processors = rows.Select(r =>
        {
            var name = CpuRules.CleanName(r.String("Name"));
            var (generation, number) = CpuRules.Generation(name);
            return new ProcessorInfo
            {
                Name = name,
                Manufacturer = CpuRules.Vendor(r.String("Manufacturer"), name),
                Cores = r.Int("NumberOfCores"),
                LogicalProcessors = r.Int("NumberOfLogicalProcessors"),
                BaseClockMhz = r.Int("MaxClockSpeed") is > 0 and var max ? max : null,
                CurrentClockMhz = r.Int("CurrentClockSpeed") is > 0 and var cur ? cur : null,
                L2CacheKb = r.Int("L2CacheSize") is > 0 and var l2 ? l2 : null,
                L3CacheKb = r.Int("L3CacheSize") is > 0 and var l3 ? l3 : null,
                Socket = r.String("SocketDesignation"),
                Architecture = CpuRules.Architecture(r.Int("Architecture")),
                VirtualizationEnabled = r.Bool("VirtualizationFirmwareEnabled"),
                Generation = generation,
                GenerationNumber = number,
            };
        }).ToList();

        return new CpuInfo { Processors = processors };
    }
}

/// <summary>Pure processor rules (name cleanup, vendor, generation hint).</summary>
public static partial class CpuRules
{
    /// <summary>Removes "(R)", "(TM)", "CPU", "@ 2.40GHz" and doubled spaces.</summary>
    public static string? CleanName(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var s = name.Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("®", "", StringComparison.Ordinal)
                    .Replace("™", "", StringComparison.Ordinal);
        s = ClockSuffix().Replace(s, "");
        s = CpuWord().Replace(s, " ");
        s = Spaces().Replace(s, " ").Trim();
        return s.Length == 0 ? null : s;
    }

    public static string? Vendor(string? manufacturer, string? name)
    {
        var text = $"{manufacturer} {name}";
        if (text.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            return "Intel";
        }

        if (text.Contains("AMD", StringComparison.OrdinalIgnoreCase) || text.Contains("Ryzen", StringComparison.OrdinalIgnoreCase))
        {
            return "AMD";
        }

        if (text.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase) || text.Contains("Snapdragon", StringComparison.OrdinalIgnoreCase))
        {
            return "Qualcomm";
        }

        return manufacturer;
    }

    public static string? Architecture(int? code) => code switch
    {
        0 => "x86",
        5 => "ARM",
        9 => "x64",
        12 => "ARM64",
        _ => null,
    };

    /// <summary>
    /// Generation hint from the model name: "Core i5-1235U" is 12th gen, "Core i7-8650U" 8th gen, "Core Ultra 7 155H"
    /// Core Ultra Series 1, "Ryzen 5 5600X" Ryzen 5000 series, "Ryzen AI 9 HX 370" Ryzen AI 300 series.
    /// </summary>
    public static (string? Text, int? Number) Generation(string? name)
    {
        if (name is null)
        {
            return (null, null);
        }

        var ultra = CoreUltra().Match(name);
        if (ultra.Success)
        {
            var series = ultra.Groups["num"].Value[0] switch
            {
                '1' => 1,
                '2' => 2,
                '3' => 3,
                _ => (int?)null,
            };
            return series is null ? ("Intel Core Ultra", null) : ($"Intel Core Ultra (Series {series})", series);
        }

        // Newer names say it outright: "13th Gen Intel Core i5-1335U".
        var stated = StatedGeneration().Match(name);
        if (stated.Success && name.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            var gen = int.Parse(stated.Groups["num"].Value, System.Globalization.CultureInfo.InvariantCulture);
            return ($"{Ordinal(gen)} gen Intel Core", gen);
        }

        var core = IntelCore().Match(name);
        if (core.Success)
        {
            var digits = core.Groups["num"].Value;
            int? gen = digits.Length switch
            {
                // Desktop and H-series 10th gen onwards: i5-10400, i7-12700H.
                5 => int.Parse(digits[..2], System.Globalization.CultureInfo.InvariantCulture),
                // Mobile 10th-14th gen use 4 digits starting with 1: i5-1035G1, i5-1235U, i5-1335U.
                4 when digits[0] == '1' => int.Parse(digits[..2], System.Globalization.CultureInfo.InvariantCulture),
                // 2nd-9th gen: i7-8650U, i5-2500K.
                4 => int.Parse(digits[..1], System.Globalization.CultureInfo.InvariantCulture),
                // 3-digit models are 1st gen (i5-520M).
                3 => 1,
                _ => null,
            };
            return gen is null ? (null, null) : ($"{Ordinal(gen.Value)} gen Intel Core", gen);
        }

        // Newer Intel naming without "i": "Intel Core 7 150U" (Series 1 non-Ultra).
        var coreN = IntelCoreSeries().Match(name);
        if (coreN.Success)
        {
            return ("Intel Core (Series 1)", 1);
        }

        var ryzenAi = RyzenAi().Match(name);
        if (ryzenAi.Success)
        {
            var hundreds = int.Parse(ryzenAi.Groups["num"].Value[..1], System.Globalization.CultureInfo.InvariantCulture) * 100;
            return ($"Ryzen AI {hundreds} series", hundreds);
        }

        var ryzen = Ryzen().Match(name);
        if (ryzen.Success)
        {
            var digits = ryzen.Groups["num"].Value;
            var thousands = int.Parse(digits[..1], System.Globalization.CultureInfo.InvariantCulture) * 1000;
            return ($"Ryzen {thousands} series", thousands);
        }

        if (name.Contains("Snapdragon X", StringComparison.OrdinalIgnoreCase))
        {
            return ("Snapdragon X series", null);
        }

        return (null, null);
    }

    private static string Ordinal(int n) => (n % 100) switch
    {
        11 or 12 or 13 => $"{n}th",
        _ => (n % 10) switch
        {
            1 => $"{n}st",
            2 => $"{n}nd",
            3 => $"{n}rd",
            _ => $"{n}th",
        },
    };

    [GeneratedRegex(@"\s*@\s*[\d.]+\s*GHz", RegexOptions.IgnoreCase)]
    private static partial Regex ClockSuffix();

    [GeneratedRegex(@"\s(CPU|Processor)(\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex CpuWord();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"Core\s+Ultra\s+\d\s+(?<num>\d{3})", RegexOptions.IgnoreCase)]
    private static partial Regex CoreUltra();

    [GeneratedRegex(@"\b(?<num>\d{1,2})(st|nd|rd|th)\s+Gen\b", RegexOptions.IgnoreCase)]
    private static partial Regex StatedGeneration();

    [GeneratedRegex(@"Core\s*i[3579]-(?<num>\d{3,5})", RegexOptions.IgnoreCase)]
    private static partial Regex IntelCore();

    [GeneratedRegex(@"Intel\s+Core\s+[3579]\s+\d{3}[A-Z]", RegexOptions.IgnoreCase)]
    private static partial Regex IntelCoreSeries();

    [GeneratedRegex(@"Ryzen\s+AI\s+(?:\d\s+)?(?:HX\s+|PRO\s+)*(?<num>\d{3})", RegexOptions.IgnoreCase)]
    private static partial Regex RyzenAi();

    [GeneratedRegex(@"Ryzen\s+(?:Threadripper\s+)?(?:PRO\s+)?\d?\s*(?:PRO\s+)?(?<num>\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex Ryzen();
}

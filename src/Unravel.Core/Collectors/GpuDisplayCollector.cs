using Unravel.Core.Infrastructure;
using Unravel.Core.Models;

namespace Unravel.Core.Collectors;

/// <summary>GPUs (Win32_VideoController + display driver registry) and connected monitors (EDID via root\wmi).</summary>
public sealed class GpuDisplayCollector : CollectorBase<GraphicsInfo>
{
    public override string Section => Sections.Graphics;

    /// <summary>The display adapter device class; each numbered subkey is one installed adapter driver.</summary>
    private const string DisplayClass = @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    protected override GraphicsInfo Collect(CollectContext ctx, CollectorNotes notes, CancellationToken ct)
    {
        var controllers = TryQuery(ctx, notes, "graphics adapters", WmiNamespaces.Cimv2, "Win32_VideoController",
        [
            "Name", "AdapterRAM", "DriverVersion", "DriverDate", "CurrentHorizontalResolution", "CurrentVerticalResolution",
            "CurrentRefreshRate", "PNPDeviceID",
        ]);
        ct.ThrowIfCancellationRequested();

        // AdapterRAM is a 32-bit value (capped at 4 GB); the driver key has the real 64-bit size.
        var driverMemory = ctx.Source.RegistrySubKeys(DisplayClass)
            .Where(k => k.All(char.IsDigit))
            .Select(k => ctx.Source.RegistryValues($@"{DisplayClass}\{k}"))
            .Where(v => v.TryGetValue("DriverDesc", out var d) && d is string)
            .Select(v => (Desc: (string)v["DriverDesc"]!, Bytes: GraphicsRules.MemorySize(v)))
            .ToList();

        var gpus = controllers
            // Remote desktop and "Microsoft Basic Display" placeholders only when nothing else is there.
            .Select(c =>
            {
                var name = c.String("Name");
                var fromDriver = driverMemory.FirstOrDefault(d => string.Equals(d.Desc, name, StringComparison.OrdinalIgnoreCase)).Bytes;
                var adapterRam = c.ULong("AdapterRAM") is > 0 and var ram ? ram : (ulong?)null;
                return new GpuInfo
                {
                    Name = name,
                    VramBytes = fromDriver is > 0 ? (ulong)fromDriver.Value : adapterRam,
                    DriverVersion = c.String("DriverVersion"),
                    DriverDate = c.Date("DriverDate"),
                    CurrentMode = GraphicsRules.Mode(c.Int("CurrentHorizontalResolution"), c.Int("CurrentVerticalResolution"), c.Int("CurrentRefreshRate")),
                };
            })
            .ToList();
        if (gpus.Count > 1)
        {
            gpus = gpus.Where(g => !GraphicsRules.IsPlaceholderAdapter(g.Name)).DefaultIfEmpty(gpus[0]).ToList();
        }

        var monitors = ReadMonitors(ctx, notes);
        if (gpus.Count == 0 && monitors.Count == 0)
        {
            throw new InvalidOperationException(notes.Warnings.LastOrDefault() ?? "WMI returned no graphics adapters");
        }

        return new GraphicsInfo { Gpus = gpus, Monitors = monitors };
    }

    private static List<MonitorInfo> ReadMonitors(CollectContext ctx, CollectorNotes notes)
    {
        var ids = TryQuery(ctx, notes, "monitors", WmiNamespaces.Wmi, "WmiMonitorID",
            ["InstanceName", "Active", "ManufacturerName", "ProductCodeID", "SerialNumberID", "UserFriendlyName", "YearOfManufacture"]);
        if (ids.Count == 0)
        {
            return [];
        }

        // Size and connection are optional extras: failures here are not worth a warning.
        static Dictionary<string, WmiRecord> ByInstance(IEnumerable<WmiRecord> rows) => rows
            .Where(r => r.RawString("InstanceName") is not null)
            .GroupBy(r => r.RawString("InstanceName")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var sizes = ByInstance(TryQuery(ctx, new CollectorNotes(), "monitor sizes", WmiNamespaces.Wmi, "WmiMonitorBasicDisplayParams",
            ["InstanceName", "MaxHorizontalImageSize", "MaxVerticalImageSize"]));
        var connections = ByInstance(TryQuery(ctx, new CollectorNotes(), "monitor connections", WmiNamespaces.Wmi, "WmiMonitorConnectionParams",
            ["InstanceName", "VideoOutputTechnology"]));

        return ids
            .Where(m => m.Bool("Active") != false)
            .Select(m =>
            {
                var instance = m.RawString("InstanceName") ?? "";
                var size = sizes.GetValueOrDefault(instance);
                var tech = connections.GetValueOrDefault(instance)?.Long("VideoOutputTechnology");
                var code = GraphicsRules.EdidString(m.LongArray("ManufacturerName"));
                return new MonitorInfo
                {
                    Make = GraphicsRules.MonitorMake(code),
                    Model = OemValues.Clean(GraphicsRules.EdidString(m.LongArray("UserFriendlyName")))
                            ?? OemValues.Clean(GraphicsRules.EdidString(m.LongArray("ProductCodeID"))),
                    SerialNumber = OemValues.Clean(GraphicsRules.EdidString(m.LongArray("SerialNumberID"))),
                    YearOfManufacture = m.Int("YearOfManufacture") is > 1990 and var y ? y : null,
                    SizeInches = GraphicsRules.Diagonal(size?.Int("MaxHorizontalImageSize"), size?.Int("MaxVerticalImageSize")),
                    Connection = GraphicsRules.Connection(tech),
                    IsInternal = GraphicsRules.IsInternal(tech),
                };
            })
            .OrderByDescending(m => m.IsInternal)
            .ToList();
    }
}

/// <summary>Pure graphics rules (EDID decoding, connection types, memory size).</summary>
public static class GraphicsRules
{
    /// <summary>EDID strings come as arrays of character codes padded with zeros.</summary>
    public static string? EdidString(IReadOnlyList<long> codes)
    {
        var chars = codes.TakeWhile(c => c != 0).Where(c => c is >= 32 and < 127).Select(c => (char)c).ToArray();
        var s = new string(chars).Trim();
        return s.Length == 0 ? null : s;
    }

    private static readonly Dictionary<string, string> PnpVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ACI"] = "ASUS", ["ACR"] = "Acer", ["AOC"] = "AOC", ["AUO"] = "AU Optronics", ["BNQ"] = "BenQ", ["BOE"] = "BOE",
        ["CMN"] = "Chimei Innolux", ["CMO"] = "Chi Mei", ["DEL"] = "Dell", ["ENC"] = "Eizo", ["GSM"] = "LG", ["GBT"] = "Gigabyte",
        ["HPN"] = "HP", ["HWP"] = "HP", ["HKC"] = "HKC", ["IVM"] = "iiyama", ["IVO"] = "InfoVision", ["LEN"] = "Lenovo",
        ["LGD"] = "LG Display", ["LPL"] = "LG Philips", ["MEI"] = "Panasonic", ["MSI"] = "MSI", ["MSF"] = "Microsoft",
        ["NEC"] = "NEC", ["PHL"] = "Philips", ["SAM"] = "Samsung", ["SDC"] = "Samsung Display", ["SEC"] = "Samsung",
        ["SHP"] = "Sharp", ["SNY"] = "Sony", ["VSC"] = "ViewSonic", ["AAA"] = "Avolites", ["XMI"] = "Xiaomi", ["HSD"] = "HannStar",
        ["TMX"] = "Tianma", ["CSO"] = "CSOT", ["CSW"] = "CSOT", ["NCP"] = "Najing CEC Panda", ["APP"] = "Apple", ["FUS"] = "Fujitsu", ["MZI"] = "Medion",
    };

    public static string? MonitorMake(string? pnpCode) =>
        pnpCode is null ? null : PnpVendors.TryGetValue(pnpCode, out var name) ? name : pnpCode;

    /// <summary>Diagonal in inches from the EDID image size in centimetres (0 = not reported, e.g. projectors).</summary>
    public static double? Diagonal(int? widthCm, int? heightCm) =>
        widthCm is > 0 && heightCm is > 0 ? Math.Round(Math.Sqrt(widthCm.Value * widthCm.Value + heightCm.Value * heightCm.Value) / 2.54, 1) : null;

    /// <summary>D3DKMDT_VIDEO_OUTPUT_TECHNOLOGY to a short name.</summary>
    public static string? Connection(long? tech) => tech switch
    {
        null => null,
        0 => "VGA",
        1 => "S-Video",
        2 or 3 or 4 => tech == 4 ? "DVI" : "Composite",
        5 => "HDMI",
        6 => "LVDS (built-in)",
        8 => "D-Jpn",
        9 => "SDI",
        10 => "DisplayPort",
        11 => "Built-in (eDP)",
        12 => "UDI",
        13 => "UDI (built-in)",
        14 => "SDTV dongle",
        15 => "Miracast (wireless)",
        16 => "Built-in (indirect)",
        17 => "Indirect (USB / virtual)",
        0x80000000 => "Built-in",
        _ => "Other",
    };

    public static bool IsInternal(long? tech) => tech is 6 or 11 or 13 or 16 or 0x80000000;

    public static string? Mode(int? width, int? height, int? refresh)
    {
        if (width is not > 0 || height is not > 0)
        {
            return null;
        }

        return refresh is > 1 ? $"{width} × {height} @ {refresh} Hz" : $"{width} × {height}";
    }

    /// <summary>HardwareInformation.qwMemorySize (QWORD) or HardwareInformation.MemorySize (DWORD or 4-byte binary).</summary>
    public static long? MemorySize(IReadOnlyDictionary<string, object?> driverValues)
    {
        foreach (var name in new[] { "HardwareInformation.qwMemorySize", "HardwareInformation.MemorySize" })
        {
            if (driverValues.TryGetValue(name, out var v) && CollectorBase<GraphicsInfo>.ToLong(v) is > 0 and var bytes)
            {
                return bytes;
            }
        }

        return null;
    }

    public static bool IsPlaceholderAdapter(string? name) =>
        name is null
        || name.Contains("Remote Display", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Indirect Display", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Virtual Display", StringComparison.OrdinalIgnoreCase);
}

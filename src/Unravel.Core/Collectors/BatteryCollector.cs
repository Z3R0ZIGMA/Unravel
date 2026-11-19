using System.Xml.Linq;
using Unravel.Core.Infrastructure;
using Unravel.Core.Models;

namespace Unravel.Core.Collectors;

/// <summary>
/// Battery summary: design vs full-charge capacity, health and cycle count (plan.md section 4.3). The same root\wmi
/// classes as Unveil; when BatteryStaticData needs admin, design capacity comes from `powercfg /batteryreport`.
/// "Open in Unveil" in the app gives the deep dive.
/// </summary>
public sealed class BatteryCollector : CollectorBase<BatteryInfo>
{
    public override string Section => Sections.Battery;

    private const long Unknown = 0xFFFFFFFF;

    protected override BatteryInfo Collect(CollectContext ctx, CollectorNotes notes, CancellationToken ct)
    {
        // Read directly: a failed query is a failure, while an empty answer means a desktop without a battery.
        var win32 = ctx.Source.Query(WmiNamespaces.Cimv2, "Win32_Battery",
            ["Name", "DeviceID", "EstimatedChargeRemaining", "DesignCapacity", "FullChargeCapacity"]);
        if (win32.Count == 0)
        {
            return new BatteryInfo();
        }

        ct.ThrowIfCancellationRequested();
        var quiet = new CollectorNotes();
        var full = TryQuery(ctx, quiet, "battery capacity", WmiNamespaces.Wmi, "BatteryFullChargedCapacity", ["InstanceName", "FullChargedCapacity"]);
        var cycles = TryQuery(ctx, quiet, "battery cycles", WmiNamespaces.Wmi, "BatteryCycleCount", ["InstanceName", "CycleCount"]);
        var stat = TryQuery(ctx, quiet, "battery design", WmiNamespaces.Wmi, "BatteryStaticData",
            ["InstanceName", "DesignedCapacity", "ManufactureName", "DeviceName"]);

        // Batteries are matched by position: root\wmi and Win32_Battery list them in the same order.
        var report = stat.Count == 0 ? BatteryRules.ParseReport(TryRows(ctx, quiet, "battery report", RowKinds.BatteryReport).FirstOrDefault()?.RawString("Xml")) : [];

        var batteries = win32.Select((w, i) =>
        {
            var s = i < stat.Count ? stat[i] : null;
            var r = i < report.Count ? report[i] : null;
            return new BatterySummary
            {
                Name = s?.String("DeviceName") ?? r?.Name ?? w.String("Name"),
                Manufacturer = s?.String("ManufactureName") ?? r?.Manufacturer,
                DesignCapacityMWh = Known(s?.Long("DesignedCapacity")) ?? r?.DesignCapacityMWh ?? Known(w.Long("DesignCapacity")),
                FullChargeCapacityMWh = Known(i < full.Count ? full[i].Long("FullChargedCapacity") : null) ?? r?.FullChargeCapacityMWh
                                        ?? Known(w.Long("FullChargeCapacity")),
                CycleCount = Known(i < cycles.Count ? cycles[i].Long("CycleCount") : null) is > 0 and var c ? (int)c : r?.CycleCount,
                ChargePercent = w.Int("EstimatedChargeRemaining") is >= 0 and <= 100 and var pct ? pct : null,
            };
        }).ToList();

        if (batteries.Any(b => b.DesignCapacityMWh is null))
        {
            notes.Warn("Couldn't read the battery design capacity, so battery health is unknown");
        }

        return new BatteryInfo { Batteries = batteries };
    }

    private static long? Known(long? value) => value is null or 0 or Unknown ? null : value;
}

public static class BatteryRules
{
    /// <summary>Reads the batteries from a `powercfg /batteryreport /xml` report (element names, any namespace).</summary>
    public static IReadOnlyList<BatterySummary> ParseReport(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return [];
        }

        try
        {
            var doc = XDocument.Parse(xml);
            return doc.Descendants().Where(e => e.Name.LocalName == "Battery" && e.Parent?.Name.LocalName == "Batteries")
                .Select(b =>
                {
                    string? Text(string name) => OemValues.Clean(b.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value);
                    long? Number(string name) => long.TryParse(Text(name), out var n) && n > 0 ? n : null;
                    return new BatterySummary
                    {
                        Name = Text("Id"),
                        Manufacturer = Text("Manufacturer"),
                        DesignCapacityMWh = Number("DesignCapacity"),
                        FullChargeCapacityMWh = Number("FullChargeCapacity"),
                        CycleCount = (int?)Number("CycleCount"),
                    };
                })
                .ToList();
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }
}

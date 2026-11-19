using System.Globalization;
using Unravel.Core.Infrastructure;
using Unravel.Core.Models;

namespace Unravel.Core.Collectors;

/// <summary>Windows Update history and service state (plan.md section 4.4). Pending restart is in the OS section.</summary>
public sealed class UpdatesCollector : CollectorBase<UpdatesInfo>
{
    public override string Section => Sections.Updates;

    protected override UpdatesInfo Collect(CollectContext ctx, CollectorNotes notes, CancellationToken ct)
    {
        var history = TryRows(ctx, notes, "Windows Update history", RowKinds.UpdateHistory)
            .Select(r => (Title: r.RawString("Title"), Date: DateTimeOffset.TryParse(r.RawString("Date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateTimeOffset?)null,
                Result: r.Int("Result"), Operation: r.Int("Operation")))
            .Where(r => r.Title is not null && r.Date is { Year: > 2000 })
            .Select(r => new UpdateEntry(r.Title!, r.Date!.Value, r.Result is 2 or 3, r.Operation == 2))
            .OrderByDescending(u => u.Date)
            .ToList();
        ct.ThrowIfCancellationRequested();

        var service = TryQuery(ctx, notes, "Windows Update service", WmiNamespaces.Cimv2, "Win32_Service", ["Name", "StartMode", "State"], "Name = 'wuauserv'")
            .FirstOrDefault();

        return new UpdatesInfo
        {
            History = history,
            ServiceStartMode = service?.RawString("StartMode"),
            ServiceState = service?.RawString("State"),
        };
    }
}

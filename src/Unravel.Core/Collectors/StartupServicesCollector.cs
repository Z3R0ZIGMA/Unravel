using System.Text.RegularExpressions;
using Unravel.Core.Infrastructure;
using Unravel.Core.Models;

namespace Unravel.Core.Collectors;

/// <summary>Startup programs (Run keys, Startup folders, logon tasks) and services (plan.md section 4.7).</summary>
public sealed class StartupServicesCollector : CollectorBase<StartupServicesInfo>
{
    public override string Section => Sections.Startup;

    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    protected override StartupServicesInfo Collect(CollectContext ctx, CollectorNotes notes, CancellationToken ct)
    {
        var items = new List<StartupItem>();
        var publishers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        string? Publisher(string? command)
        {
            var exe = StartupRules.ExecutablePath(command);
            if (exe is null)
            {
                return null;
            }

            if (!publishers.TryGetValue(exe, out var company))
            {
                company = OemValues.Clean(ctx.Source.Rows(RowKinds.FileVersion, exe).FirstOrDefault()?.RawString("CompanyName"));
                publishers[exe] = company;
            }

            return company;
        }

        // Run keys: (key, approval key, location label)
        foreach (var (key, approval, location) in new[]
        {
            (@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", $@"HKLM\{Approved}\Run", "Registry (all users)"),
            (@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", $@"HKLM\{Approved}\Run32", "Registry (all users, 32-bit)"),
            (@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", $@"HKCU\{Approved}\Run", "Registry (this user)"),
        })
        {
            var approvals = ctx.Source.RegistryValues(approval);
            foreach (var (name, value) in ctx.Source.RegistryValues(key))
            {
                if (value is not string command || name.Length == 0)
                {
                    continue;
                }

                items.Add(new StartupItem
                {
                    Name = name,
                    Command = command,
                    Location = location,
                    Enabled = StartupRules.IsApproved(approvals.GetValueOrDefault(name)),
                    Publisher = Publisher(command),
                });
            }
        }

        ct.ThrowIfCancellationRequested();

        // Startup folders
        foreach (var (folder, approval, location) in new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), $@"HKLM\{Approved}\StartupFolder", "Startup folder (all users)"),
            (Environment.GetFolderPath(Environment.SpecialFolder.Startup), $@"HKCU\{Approved}\StartupFolder", "Startup folder (this user)"),
        })
        {
            if (folder.Length == 0)
            {
                continue;
            }

            var approvals = ctx.Source.RegistryValues(approval);
            foreach (var file in ctx.Source.Rows(RowKinds.FolderFiles, folder))
            {
                var name = file.RawString("Name");
                if (name is null)
                {
                    continue;
                }

                items.Add(new StartupItem
                {
                    Name = Path.GetFileNameWithoutExtension(name),
                    Command = file.RawString("Path"),
                    Location = location,
                    Enabled = StartupRules.IsApproved(approvals.GetValueOrDefault(name)),
                    Publisher = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Publisher(file.RawString("Path")) : null,
                });
            }
        }

        // Scheduled tasks at logon / startup (non-Microsoft folders)
        foreach (var task in TryRows(ctx, notes, "scheduled tasks", RowKinds.StartupTasks))
        {
            var command = task.RawString("Command");
            items.Add(new StartupItem
            {
                Name = task.RawString("Name") ?? "?",
                Command = command,
                Location = $"Scheduled task ({task.RawString("Trigger")?.ToLowerInvariant() ?? "at logon"})",
                Enabled = task.Bool("Enabled") != false,
                Publisher = Publisher(command) ?? OemValues.Clean(task.RawString("Author")),
            });
        }

        ct.ThrowIfCancellationRequested();
        var (thirdParty, stopped) = ReadServices(ctx, notes);

        return new StartupServicesInfo
        {
            StartupItems = items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            ThirdPartyServices = thirdParty,
            StoppedAutomatic = stopped,
        };
    }

    private static (List<ServiceItem> ThirdParty, List<ServiceItem> Stopped) ReadServices(CollectContext ctx, CollectorNotes notes)
    {
        var rows = TryQuery(ctx, notes, "services", WmiNamespaces.Cimv2, "Win32_Service",
            ["Name", "DisplayName", "State", "StartMode", "PathName", "StartName", "DelayedAutoStart"]);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var services = rows.Select(r =>
        {
            var name = r.RawString("Name") ?? "?";
            // WMI hides the path of some services from a standard user; the service key still has it.
            var path = r.RawString("PathName") ?? RegString(ctx, $@"HKLM\SYSTEM\CurrentControlSet\Services\{name}", "ImagePath");
            var exe = StartupRules.ExecutablePath(path);
            // Programs under C:\Windows are Windows' own (svchost, lsass...), except drivers' helpers in the DriverStore.
            var inWindows = exe is not null && exe.StartsWith(windows, StringComparison.OrdinalIgnoreCase)
                            && !exe.Contains(@"\DriverStore\", StringComparison.OrdinalIgnoreCase);
            var company = inWindows || exe is null ? null : OemValues.Clean(ctx.Source.Rows(RowKinds.FileVersion, exe).FirstOrDefault()?.RawString("CompanyName"));
            var mode = r.RawString("StartMode");
            var delayed = r.Bool("DelayedAutoStart") == true;
            return new ServiceItem
            {
                Name = name,
                DisplayName = r.RawString("DisplayName"),
                State = r.RawString("State"),
                StartMode = mode switch
                {
                    "Auto" => delayed ? "Automatic (delayed)" : "Automatic",
                    "Manual" => "Manual",
                    "Disabled" => "Disabled",
                    var m => m,
                },
                Account = r.RawString("StartName"),
                Path = path,
                Company = company,
                IsMicrosoft = inWindows || company?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true,
            };
        }).ToList();

        var thirdParty = services.Where(s => !s.IsMicrosoft).OrderBy(s => s.DisplayName ?? s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var stopped = services
            .Where(s => s.StartMode?.StartsWith("Automatic", StringComparison.Ordinal) == true && s.State is not "Running")
            .Select(s => s with { TriggerStart = ctx.Source.RegistryKeyExists($@"HKLM\SYSTEM\CurrentControlSet\Services\{s.Name}\TriggerInfo") })
            .OrderBy(s => s.TriggerStart)
            .ThenBy(s => s.DisplayName ?? s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return (thirdParty, stopped);
    }
}

public static partial class StartupRules
{
    /// <summary>
    /// StartupApproved values: the first byte is even (02, 06) when enabled and odd (03, 07) when turned off in Task Manager.
    /// No value means it was never changed: enabled.
    /// </summary>
    public static bool IsApproved(object? value)
    {
        var first = value switch
        {
            byte[] { Length: > 0 } b => b[0],
            object[] { Length: > 0 } a => Convert.ToInt32(a[0], System.Globalization.CultureInfo.InvariantCulture),
            _ => -1,
        };
        return first < 0 || (first & 1) == 0;
    }

    /// <summary>The program in a command line: "C:\Program Files\X\x.exe" /background, or C:\Tools\y.exe -m, or rundll32 a.dll.</summary>
    public static string? ExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var c = Environment.ExpandEnvironmentVariables(command.Trim());
        if (c.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            c = c[4..];
        }

        // Kernel-style paths in service keys: "\SystemRoot\System32\..." and "System32\drivers\x.sys".
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (c.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            c = Path.Combine(windows, c[12..]);
        }
        else if (c.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
        {
            c = Path.Combine(windows, c);
        }

        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : null;
        }

        var m = ExePath().Match(c);
        return m.Success ? m.Value : c.Split(' ')[0];
    }

    [GeneratedRegex(@"^.+?\.(exe|com|bat|cmd)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExePath();
}

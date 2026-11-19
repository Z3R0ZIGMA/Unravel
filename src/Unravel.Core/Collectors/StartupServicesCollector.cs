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
        var versions = new Dictionary<string, WmiRecord?>(StringComparer.OrdinalIgnoreCase);

        // The program's version information (company, description), read once per program.
        WmiRecord? Version(string? command)
        {
            var exe = StartupRules.ExecutablePath(command);
            if (exe is null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!versions.TryGetValue(exe, out var version))
            {
                version = ctx.Source.Rows(RowKinds.FileVersion, exe).FirstOrDefault();
                versions[exe] = version;
            }

            return version;
        }

        // Named as Task Manager names it: by the program's description, not the registry value or task name.
        StartupItem Item(string entry, string? command, string location, bool enabled, string? author = null)
        {
            var version = Version(command);
            return new StartupItem
            {
                Name = StartupRules.DisplayName(entry, StartupRules.ExecutablePath(command),
                    OemValues.Clean(version?.RawString("FileDescription")), OemValues.Clean(version?.RawString("ProductName"))),
                EntryName = entry,
                Command = command,
                Location = location,
                Enabled = enabled,
                Publisher = OemValues.Clean(version?.RawString("CompanyName")) ?? author,
            };
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

                items.Add(Item(name, command, location, StartupRules.IsApproved(approvals.GetValueOrDefault(name))));
            }
        }

        ct.ThrowIfCancellationRequested();
        if (ctx.NoSignedInUser)
        {
            notes.Warn("Nobody is signed in, so startup programs set for a user are not listed");
        }

        // Startup folders (as Local System, the signed-in user's; see CollectContext.UserStartupFolder)
        foreach (var (folder, approval, location) in new[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), $@"HKLM\{Approved}\StartupFolder", "Startup folder (all users)"),
            (ctx.UserStartupFolder ?? "", $@"HKCU\{Approved}\StartupFolder", "Startup folder (this user)"),
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

                items.Add(Item(name, file.RawString("Path"), location, StartupRules.IsApproved(approvals.GetValueOrDefault(name))));
            }
        }

        // Scheduled tasks at logon / startup (non-Microsoft folders)
        foreach (var task in TryRows(ctx, notes, "scheduled tasks", RowKinds.StartupTasks))
        {
            items.Add(Item(task.RawString("Name") ?? "?", task.RawString("Command"),
                $"Scheduled task ({task.RawString("Trigger")?.ToLowerInvariant() ?? "at logon"})",
                task.Bool("Enabled") != false, OemValues.Clean(task.RawString("Author"))));
        }

        ct.ThrowIfCancellationRequested();
        var (thirdParty, stopped) = ReadServices(ctx, notes);

        return new StartupServicesInfo
        {
            StartupItems = StartupRules.MergeDuplicates(items).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList(),
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

    /// <summary>Hosts whose own description says nothing about what they start ("Windows host process (Rundll32)").</summary>
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "rundll32.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "msiexec.exe",
        "conhost.exe", "explorer.exe", "regsvr32.exe", "javaw.exe", "java.exe", "python.exe", "pythonw.exe", "node.exe",
    };

    /// <summary>
    /// What to call a startup entry, as Task Manager does: the program's description ("ASUS ExpertPanel"), else its
    /// product name, else the entry's own name without ".exe", ".lnk" or a user's SID ("OneDrive Startup Task").
    /// </summary>
    public static string DisplayName(string entry, string? exe, string? description, string? product)
    {
        var fileName = exe is null ? null : Path.GetFileName(exe);
        // Not empty, not just the file name, and not a template left in by the developer ("TODO: <product name>").
        bool Useful(string? text) => !string.IsNullOrWhiteSpace(text) && text.Trim().Length >= 3
            && !text.Contains("TODO", StringComparison.Ordinal) && text.IndexOfAny(['<', '>']) < 0
            && !string.Equals(text.Trim(), fileName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(text.Trim(), Path.GetFileNameWithoutExtension(fileName), StringComparison.OrdinalIgnoreCase);

        if (fileName is null || !Hosts.Contains(fileName))
        {
            if (Useful(description))
            {
                return description!.Trim();
            }

            if (Useful(product))
            {
                return product!.Trim();
            }
        }

        return CleanEntryName(entry);
    }

    /// <summary>"panel.exe" to "panel", "OneDrive Startup Task-S-1-5-21-...-1001" to "OneDrive Startup Task".</summary>
    public static string CleanEntryName(string entry)
    {
        var name = Sid().Replace(entry, "");
        name = Guid().Replace(name, "");
        foreach (var extension in new[] { ".exe", ".lnk" })
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^extension.Length];
            }
        }

        name = name.Trim(' ', '-', '_');
        return name.Length == 0 ? entry : name;
    }

    /// <summary>
    /// One row per program: the same command in the all-users and this-user Run keys (some installers write both)
    /// becomes one item, "Registry (all users, this user)", on when either copy is on.
    /// </summary>
    public static IEnumerable<StartupItem> MergeDuplicates(IEnumerable<StartupItem> items) =>
        items.GroupBy(i => i.Command is null ? $"|{i.Location}|{i.EntryName}" : NormalizeCommand(i.Command), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() == 1 ? g.First() : g.First() with
            {
                Location = MergeLocations(g.Select(i => i.Location)),
                Enabled = g.Any(i => i.Enabled),
                Publisher = g.Select(i => i.Publisher).FirstOrDefault(p => p is not null),
            });

    private static string NormalizeCommand(string command) =>
        string.Join(' ', Environment.ExpandEnvironmentVariables(command).Replace("\"", "", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>["Registry (all users)", "Registry (this user)"] to "Registry (all users, this user)".</summary>
    public static string MergeLocations(IEnumerable<string> locations) =>
        string.Join("; ", locations.Distinct(StringComparer.Ordinal)
            .Select(l => LocationParts().Match(l))
            .GroupBy(m => m.Success ? m.Groups["kind"].Value : m.Value, StringComparer.Ordinal)
            .Select(g => g.All(m => m.Success) ? $"{g.Key} ({string.Join(", ", g.Select(m => m.Groups["where"].Value))})" : g.Key));

    [GeneratedRegex(@"[-_ ]*S-1-5-21(-\d+)+")]
    private static partial Regex Sid();

    [GeneratedRegex(@"[-_ ]*\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?")]
    private static partial Regex Guid();

    [GeneratedRegex(@"^(?<kind>[^(]+?) \((?<where>[^)]+)\)$")]
    private static partial Regex LocationParts();
}

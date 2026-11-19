using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Unravel.Core.Infrastructure;

/// <summary>The live implementations of <see cref="ISystemSource.Rows"/>. Every result is copied into plain records.</summary>
internal static class LiveRows
{
    public static IReadOnlyList<WmiRecord> Read(string kind, string? argument) => kind switch
    {
        RowKinds.StoreApps => StoreApps(null),
        RowKinds.StartupTasks => StartupTasks(),
        RowKinds.FolderFiles => FolderFiles(argument),
        RowKinds.FileVersion => FileVersion(argument),
        RowKinds.BatteryReport => BatteryReport(),
        RowKinds.LocalGroupMembers => LocalGroupMembers(argument),
        RowKinds.Events => Events(argument),
        RowKinds.UpdateHistory => UpdateHistory(),
        _ => throw new ArgumentException($"Unknown row kind '{kind}'.", nameof(kind)),
    };

    // ------------------------------------------------------------------ event log

    private const int MaxEvents = 500;

    private static List<WmiRecord> Events(string? argument)
    {
        var bar = argument?.IndexOf('|', StringComparison.Ordinal) ?? -1;
        if (argument is null || bar <= 0)
        {
            return [];
        }

        var log = argument[..bar];
        var query = new System.Diagnostics.Eventing.Reader.EventLogQuery(log, System.Diagnostics.Eventing.Reader.PathType.LogName, argument[(bar + 1)..])
        {
            ReverseDirection = true,
        };

        var rows = new List<WmiRecord>();
        try
        {
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query);
            for (var i = 0; i < MaxEvents; i++)
            {
                using var e = reader.ReadEvent();
                if (e is null)
                {
                    break;
                }

                string? Data(int index)
                {
                    try
                    {
                        return e.Properties.Count > index ? Convert.ToString(e.Properties[index].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
                    }
                    catch (System.Diagnostics.Eventing.Reader.EventLogException)
                    {
                        return null;
                    }
                }

                rows.Add(Row(
                    ("Id", e.Id),
                    ("Provider", e.ProviderName),
                    ("Time", e.TimeCreated?.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture)),
                    ("Level", (int?)e.Level),
                    ("Data0", Data(0)), ("Data1", Data(1)), ("Data2", Data(2)), ("Data3", Data(3))));
            }
        }
        catch (System.Diagnostics.Eventing.Reader.EventLogNotFoundException ex)
        {
            throw new SystemSourceException($"Event log {log}: not found", false, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SystemSourceException($"Event log {log}: access denied", true, ex);
        }
        catch (System.Diagnostics.Eventing.Reader.EventLogException ex)
        {
            throw new SystemSourceException($"Event log {log}: {ex.Message.Trim()}", false, ex);
        }

        return rows;
    }

    // ------------------------------------------------------------------ Windows Update history

    // Store app updates fill much of the history and are filtered out later, so read enough to keep real ones.
    private const int MaxUpdates = 200;

    private static List<WmiRecord> UpdateHistory()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session") ?? throw new SystemSourceException("Windows Update: not available");
        dynamic? session = null;
        var rows = new List<WmiRecord>();
        try
        {
            session = Activator.CreateInstance(type)!;
            var searcher = session.CreateUpdateSearcher();
            int total = searcher.GetTotalHistoryCount();
            if (total == 0)
            {
                return rows;
            }

            var history = searcher.QueryHistory(0, Math.Min(total, MaxUpdates));
            foreach (dynamic entry in history)
            {
                DateTime date = entry.Date;
                rows.Add(Row(
                    ("Title", (string?)entry.Title),
                    ("Date", new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)).ToString("o", System.Globalization.CultureInfo.InvariantCulture)),
                    // OperationResultCode: 2 succeeded, 3 succeeded with errors, 4 failed, 5 aborted.
                    ("Result", (int)entry.ResultCode),
                    // UpdateOperation: 1 installation, 2 uninstallation.
                    ("Operation", (int)entry.Operation)));
            }
        }
        catch (COMException ex)
        {
            throw new SystemSourceException($"Windows Update history: {ex.Message.Trim()}", (uint)ex.HResult == 0x80070005, ex);
        }
        finally
        {
            if (session is not null)
            {
                Marshal.FinalReleaseComObject(session);
            }
        }

        return rows;
    }

    private static WmiRecord Row(params (string Name, object? Value)[] values) =>
        new(values.Select(v => new KeyValuePair<string, object?>(v.Name, v.Value)));

    // ------------------------------------------------------------------ Store apps

    /// <param name="userSid">Null for the current user (no admin rights needed), or another user's SID (needs admin: the system account).</param>
    internal static List<WmiRecord> StoreApps(string? userSid)
    {
        var rows = new List<WmiRecord>();
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            // Empty user SID = the current user.
            foreach (var package in manager.FindPackagesForUser(userSid ?? string.Empty))
            {
                rows.Add(Row(
                    ("Name", package.Id.Name),
                    ("DisplayName", Try(() => package.DisplayName)),
                    ("Publisher", Try(() => package.PublisherDisplayName)),
                    ("Version", $"{package.Id.Version.Major}.{package.Id.Version.Minor}.{package.Id.Version.Build}.{package.Id.Version.Revision}"),
                    ("Architecture", package.Id.Architecture.ToString()),
                    ("IsFramework", package.IsFramework),
                    ("IsResource", package.IsResourcePackage),
                    ("SignatureKind", Try(() => package.SignatureKind.ToString())),
                    ("InstalledDate", Try(() => package.InstalledDate.ToString("o", System.Globalization.CultureInfo.InvariantCulture)))));
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException or TypeLoadException)
        {
            throw new SystemSourceException($"Store apps: {ex.Message.Trim()}", ex is UnauthorizedAccessException, ex);
        }

        return rows;
    }

    private static string? Try(Func<string?> read)
    {
        try
        {
            return read();
        }
#pragma warning disable CA1031 // A package with a broken manifest must not hide the others.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ scheduled tasks at logon / startup

    private const int TriggerBoot = 8;
    private const int TriggerLogon = 9;
    private const int ActionExec = 0;
    private const int EnumHidden = 1;

    /// <summary>Non-Microsoft tasks (the \Microsoft folder is skipped) with a logon or startup trigger.</summary>
    private static List<WmiRecord> StartupTasks()
    {
        var rows = new List<WmiRecord>();
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new SystemSourceException("Task Scheduler: not available");
        dynamic? service = null;
        try
        {
            service = Activator.CreateInstance(type)!;
            service.Connect();
            Walk(service.GetFolder("\\"), rows);
        }
        catch (COMException ex)
        {
            throw new SystemSourceException($"Task Scheduler: {ex.Message.Trim()}", (uint)ex.HResult == 0x80070005, ex);
        }
        finally
        {
            if (service is not null)
            {
                Marshal.FinalReleaseComObject(service);
            }
        }

        return rows;
    }

    private static void Walk(dynamic folder, List<WmiRecord> rows)
    {
        string path = folder.Path;
        if (path.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (dynamic task in folder.GetTasks(EnumHidden))
        {
            try
            {
                var definition = task.Definition;
                var triggers = new List<string>();
                foreach (dynamic trigger in definition.Triggers)
                {
                    int t = trigger.Type;
                    if (t == TriggerLogon)
                    {
                        triggers.Add("At logon");
                    }
                    else if (t == TriggerBoot)
                    {
                        triggers.Add("At startup");
                    }
                }

                if (triggers.Count == 0)
                {
                    continue;
                }

                string? command = null;
                foreach (dynamic action in definition.Actions)
                {
                    if ((int)action.Type == ActionExec)
                    {
                        command = $"{action.Path} {action.Arguments}".Trim();
                        break;
                    }
                }

                rows.Add(Row(
                    ("Name", (string)task.Name),
                    ("Path", (string)task.Path),
                    ("Enabled", (bool)task.Enabled),
                    ("Trigger", string.Join(", ", triggers.Distinct())),
                    ("Command", command),
                    ("Author", (string?)definition.RegistrationInfo.Author)));
            }
            catch (COMException)
            {
                // A task we may not read: skip it.
            }
        }

        foreach (dynamic sub in folder.GetFolders(0))
        {
            Walk(sub, rows);
        }
    }

    // ------------------------------------------------------------------ files

    private static List<WmiRecord> FolderFiles(string? folder)
    {
        if (folder is null || !Directory.Exists(folder))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(f => !string.Equals(Path.GetFileName(f), "desktop.ini", StringComparison.OrdinalIgnoreCase))
                .Select(f => Row(("Name", Path.GetFileName(f)), ("Path", f)))
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static List<WmiRecord> FileVersion(string? path)
    {
        // The path comes from the registry (a Run key the user can write, a service's ImagePath). A network path would
        // make Windows connect and sign in to that server: as Local System (RMM), as the PC's own account.
        if (path is null || !LocalPaths.IsLocal(path) || !File.Exists(path))
        {
            return [];
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return [Row(("CompanyName", info.CompanyName), ("ProductName", info.ProductName), ("FileVersion", info.FileVersion),
                ("FileDescription", info.FileDescription))];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    // ------------------------------------------------------------------ battery report

    /// <summary>Runs powercfg into a temp file, reads it and deletes it (Unravel leaves nothing behind).</summary>
    private static List<WmiRecord> BatteryReport()
    {
        var file = Path.Combine(Path.GetTempPath(), $"unravel-battery-{Guid.NewGuid():N}.xml");
        try
        {
            ProcessRunner.Run("powercfg", $"/batteryreport /xml /output \"{file}\"", TimeSpan.FromSeconds(20));
            return File.Exists(file) ? [Row(("Xml", File.ReadAllText(file)))] : [];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Best effort.
            }
        }
    }

    // ------------------------------------------------------------------ local group members

    private static List<WmiRecord> LocalGroupMembers(string? groupSid)
    {
        if (groupSid is null)
        {
            return [];
        }

        // The group name is localised ("Administratoren"): look it up from its well-known SID.
        string groupName;
        try
        {
            var account = (NTAccount)new SecurityIdentifier(groupSid).Translate(typeof(NTAccount));
            groupName = account.Value[(account.Value.IndexOf('\\', StringComparison.Ordinal) + 1)..];
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return [];
        }

        var rows = new List<WmiRecord>();
        var resume = IntPtr.Zero;
        var status = NetLocalGroupGetMembers(null, groupName, 2, out var buffer, -1, out var read, out _, ref resume);
        try
        {
            if (status != 0)
            {
                throw new SystemSourceException($"Local group {groupName}: error {status}", status == 5);
            }

            var size = Marshal.SizeOf<LocalGroupMembersInfo2>();
            for (var i = 0; i < read; i++)
            {
                var info = Marshal.PtrToStructure<LocalGroupMembersInfo2>(buffer + i * size);
                string? sid = null;
                try
                {
                    sid = new SecurityIdentifier(info.Sid).Value;
                }
                catch (ArgumentException)
                {
                    // Keep the name only.
                }

                rows.Add(Row(("Name", info.DomainAndName), ("Kind", info.SidUsage switch
                {
                    1 => "User",
                    2 or 4 or 5 => "Group",
                    6 => "Deleted account",
                    _ => "Other",
                }), ("Sid", sid)));
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                _ = NetApiBufferFree(buffer);
            }
        }

        return rows;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LocalGroupMembersInfo2
    {
        public IntPtr Sid;
        public int SidUsage;
        public string DomainAndName;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupGetMembers(string? server, string group, int level, out IntPtr buffer, int maxLength,
        out int entriesRead, out int totalEntries, ref IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}

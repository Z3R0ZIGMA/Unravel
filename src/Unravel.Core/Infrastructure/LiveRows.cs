using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Unravel.Core.Infrastructure;

/// <summary>The live implementations of <see cref="ISystemSource.Rows"/>. Every result is copied into plain records.</summary>
internal static class LiveRows
{
    public static IReadOnlyList<WmiRecord> Read(string kind, string? argument) => kind switch
    {
        RowKinds.StoreApps => StoreApps(),
        RowKinds.StartupTasks => StartupTasks(),
        RowKinds.FolderFiles => FolderFiles(argument),
        RowKinds.FileVersion => FileVersion(argument),
        RowKinds.BatteryReport => BatteryReport(),
        RowKinds.LocalGroupMembers => LocalGroupMembers(argument),
        _ => throw new ArgumentException($"Unknown row kind '{kind}'.", nameof(kind)),
    };

    private static WmiRecord Row(params (string Name, object? Value)[] values) =>
        new(values.Select(v => new KeyValuePair<string, object?>(v.Name, v.Value)));

    // ------------------------------------------------------------------ Store apps

    private static List<WmiRecord> StoreApps()
    {
        var rows = new List<WmiRecord>();
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            // Empty user SID = the current user; needs no admin rights.
            foreach (var package in manager.FindPackagesForUser(string.Empty))
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
        if (path is null || !File.Exists(path))
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

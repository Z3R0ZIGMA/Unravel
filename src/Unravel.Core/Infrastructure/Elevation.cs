using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Unravel.Core.Infrastructure;

/// <summary>Works without admin, better with admin: detect elevation and relaunch elevated on request.</summary>
public static class Elevation
{
    private const int ErrorCancelled = 1223; // the user said No to the UAC prompt

    public static bool IsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts this exe again elevated (UAC prompt). Returns false when the user cancels the prompt or it fails;
    /// on true the caller should close the current instance.
    /// </summary>
    public static bool RelaunchAsAdmin(IEnumerable<string>? arguments = null)
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return false;
        }

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.CurrentDirectory,
        };
        foreach (var arg in arguments ?? [])
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using var _ = Process.Start(start);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}

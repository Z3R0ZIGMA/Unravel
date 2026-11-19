namespace Unravel.Core.Infrastructure;

/// <summary>
/// Paths Unravel may open when they come from somewhere it does not control (the registry, a startup entry).
/// </summary>
public static class LocalPaths
{
    /// <summary>
    /// True for a full path on a local drive ("C:\Program Files\x.exe"). False for network paths ("\\server\share",
    /// "\\?\UNC\..."), device and other "\\" forms, relative paths, and mapped network drives: opening those makes
    /// Windows connect to another computer and sign in to it.
    /// </summary>
    public static bool IsLocal(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        // "C:\..." and nothing else: a drive letter, a colon and a separator.
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/'))
        {
            return false;
        }

        try
        {
            return new DriveInfo(path[..1]).DriveType is not (DriveType.Network or DriveType.NoRootDirectory);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

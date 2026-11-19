using System.Diagnostics;
using System.Text;

namespace Unravel.Core.Infrastructure;

/// <summary>Runs a Windows tool with no window and captures its output, with a timeout.</summary>
public static class ProcessRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    static ProcessRunner() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Output (stdout, plus stderr when stdout is empty), or null when the tool could not run or timed out.</summary>
    public static string? Run(string file, string arguments, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(ResolveSystemTool(file), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Console tools write in the OEM code page (850/437...), not UTF-8.
            StandardOutputEncoding = OemEncoding(),
            StandardErrorEncoding = OemEncoding(),
        };

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout ?? DefaultTimeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }

                return null;
            }

            var output = stdout.GetAwaiter().GetResult();
            return string.IsNullOrWhiteSpace(output) ? stderr.GetAwaiter().GetResult() : output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs a long tool (sfc, DISM) and reports each output line as it arrives. Carriage returns count as line breaks,
    /// so "Verification 45% complete." progress updates come through. Returns the exit code, or null if it could not start.
    /// Cancelling kills the tool.
    /// </summary>
    public static async Task<int?> StreamAsync(string file, string arguments, Action<string> onLine, Encoding? encoding, CancellationToken ct)
    {
        var start = new ProcessStartInfo(ResolveSystemTool(file), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding ?? OemEncoding(),
            StandardErrorEncoding = encoding ?? OemEncoding(),
        };

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }

        if (process is null)
        {
            return null;
        }

        using (process)
        await using (ct.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        }))
        {
            var stdout = Pump(process.StandardOutput, onLine);
            var stderr = Pump(process.StandardError, onLine);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
    }

    private static async Task Pump(StreamReader reader, Action<string> onLine)
    {
        var buffer = new char[1024];
        var line = new StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c is '\r' or '\n')
                {
                    if (line.Length > 0)
                    {
                        // sfc pads its output with NULs; drop them.
                        var text = line.ToString().Replace("\0", "", StringComparison.Ordinal).Trim();
                        if (text.Length > 0)
                        {
                            onLine(text);
                        }

                        line.Clear();
                    }
                }
                else
                {
                    line.Append(c);
                }
            }
        }

        if (line.Length > 0 && line.ToString().Trim() is { Length: > 0 } last)
        {
            onLine(last);
        }
    }

    /// <summary>
    /// Always runs the real tool from System32 (Sysnative for a 32-bit process), never one found earlier on PATH or in
    /// the current folder (the exe may run from a USB stick or a share).
    /// </summary>
    private static string ResolveSystemTool(string file)
    {
        if (Path.IsPathRooted(file))
        {
            return file;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess
            ? Path.Combine(windows, "Sysnative")
            : Environment.SystemDirectory;
        var name = Path.HasExtension(file) ? file : file + ".exe";
        return Path.Combine(system, name);
    }

    private static Encoding OemEncoding()
    {
        try
        {
            return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }
}

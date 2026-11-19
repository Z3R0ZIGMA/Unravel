using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Unravel.Core.Infrastructure;

namespace Unravel.Core.Diagnostics;

/// <summary>Progress of an active test: an optional percentage, a status line, and output lines for the log.</summary>
public sealed record TestProgress(double? Percent, string Status, string? OutputLine = null);

/// <summary>What an active test found.</summary>
public sealed record TestOutcome(Severity Severity, string Summary, IReadOnlyList<string> Details);

// ====================================================================== disk speed

/// <summary>
/// Sequential write and read of a temporary file (plan.md 5.4), bypassing the Windows cache (write-through, no buffering)
/// so the disk itself is measured. The file is deleted afterwards, also when cancelled.
/// </summary>
public static class DiskSpeedTest
{
    private const int BlockSize = 4 * 1024 * 1024;
    private const int Alignment = 4096;
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    public static string Describe(int sizeMb, string folder) =>
        $"Writes a {sizeMb:N0} MB temporary file to {folder}, reads it back, then deletes it. Takes from a few seconds (NVMe) to a minute or two (hard disk).";

    public static async Task<TestOutcome> RunAsync(int sizeMb, string folder, string? diskKind, IProgress<TestProgress>? progress, CancellationToken ct)
    {
        var total = (long)sizeMb * 1024 * 1024;
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!);
        if (drive.AvailableFreeSpace < total + 2L * 1024 * 1024 * 1024)
        {
            return new(Severity.Warning, "Not enough free space for the disk test",
                [$"Needs {sizeMb:N0} MB plus 2 GB to spare on {drive.Name}; {Formatting.VolumeSize((ulong)drive.AvailableFreeSpace)} free."]);
        }

        var path = Path.Combine(folder, $"unravel-disktest-{Guid.NewGuid():N}.tmp");
        return await Task.Run(() =>
        {
            var buffer = AlignedBuffer.Allocate(BlockSize, Alignment);
            try
            {
                Random.Shared.NextBytes(buffer.Span); // random data: compressing controllers cannot cheat
                using var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    FileOptions.WriteThrough | NoBuffering | FileOptions.DeleteOnClose, total);

                var watch = Stopwatch.StartNew();
                for (long offset = 0; offset < total; offset += BlockSize)
                {
                    ct.ThrowIfCancellationRequested();
                    RandomAccess.Write(handle, buffer.Span, offset);
                    progress?.Report(new TestProgress(50.0 * (offset + BlockSize) / total, $"Writing... {Formatting.Percent(100.0 * (offset + BlockSize) / total)}"));
                }

                var writeMbs = sizeMb / watch.Elapsed.TotalSeconds;

                watch.Restart();
                for (long offset = 0; offset < total; offset += BlockSize)
                {
                    ct.ThrowIfCancellationRequested();
                    RandomAccess.Read(handle, buffer.Span, offset);
                    progress?.Report(new TestProgress(50 + 50.0 * (offset + BlockSize) / total, $"Reading... {Formatting.Percent(100.0 * (offset + BlockSize) / total)}"));
                }

                var readMbs = sizeMb / watch.Elapsed.TotalSeconds;
                return Classify(readMbs, writeMbs, diskKind);
            }
            finally
            {
                buffer.Dispose();
                // DeleteOnClose removes it; this covers a handle that never opened.
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort.
                }
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Typical sequential speeds: HDD 80-200 MB/s, SATA SSD 400-560 MB/s, NVMe 1,500+ MB/s.</summary>
    public static TestOutcome Classify(double readMbs, double writeMbs, string? diskKind)
    {
        var looksLike = readMbs switch
        {
            < 250 => "hard disk speed",
            < 700 => "SATA SSD speed",
            _ => "NVMe SSD speed",
        };
        var expected = diskKind switch
        {
            { } k when k.Contains("NVMe", StringComparison.OrdinalIgnoreCase) => (Min: 800.0, Name: "an NVMe SSD (usually 1,500+ MB/s)"),
            { } k when k.Contains("SSD", StringComparison.OrdinalIgnoreCase) => (Min: 300.0, Name: "a SATA SSD (usually 400-560 MB/s)"),
            { } k when k.Contains("HDD", StringComparison.OrdinalIgnoreCase) => (Min: 60.0, Name: "a hard disk (usually 80-200 MB/s)"),
            _ => (Min: 0.0, Name: "this disk"),
        };

        var details = new List<string>
        {
            $"Read {readMbs:N0} MB/s, write {writeMbs:N0} MB/s ({looksLike}).",
            $"Typical: hard disk 80-200 MB/s, SATA SSD 400-560 MB/s, NVMe 1,500-7,000 MB/s.",
        };
        var slow = expected.Min > 0 && Math.Min(readMbs, writeMbs) < expected.Min;
        if (slow)
        {
            details.Add($"Slower than expected for {expected.Name}: check the disk's health, free space, power plan and storage driver.");
        }

        return new(slow ? Severity.Warning : Severity.Ok, $"Read {readMbs:N0} MB/s · write {writeMbs:N0} MB/s", details);
    }

    /// <summary>A sector-aligned native buffer, as unbuffered I/O requires.</summary>
    private sealed unsafe class AlignedBuffer : IDisposable
    {
        private void* _pointer;
        private readonly int _length;

        private AlignedBuffer(void* pointer, int length)
        {
            _pointer = pointer;
            _length = length;
        }

        public static AlignedBuffer Allocate(int length, int alignment) => new(NativeMemory.AlignedAlloc((nuint)length, (nuint)alignment), length);

        public Span<byte> Span => new(_pointer, _length);

        public void Dispose()
        {
            if (_pointer is not null)
            {
                NativeMemory.AlignedFree(_pointer);
                _pointer = null;
            }
        }
    }
}

// ====================================================================== network

public sealed record PingStats(string Target, string Label, int Sent, int Received, double? AverageMs, double? MaxMs)
{
    public double LossPercent => Sent == 0 ? 100 : 100.0 * (Sent - Received) / Sent;
}

/// <summary>
/// Ping the gateway and two public DNS servers and time DNS lookups (plan.md 5.4). There is no download speed test:
/// one download from one server was not a reliable measure of the connection.
/// </summary>
public static class NetworkTest
{
    public static string Describe(string? gateway) =>
        $"Sends 10 pings each to {(gateway is null ? "" : $"your gateway ({gateway}), ")}1.1.1.1 (Cloudflare) and 8.8.8.8 (Google), and times three DNS lookups.";

    public static async Task<TestOutcome> RunAsync(string? gateway, IProgress<TestProgress>? progress, CancellationToken ct)
    {
        var targets = new List<(string Address, string Label)>();
        if (gateway is not null)
        {
            targets.Add((gateway, "Gateway"));
        }

        targets.Add(("1.1.1.1", "Internet (1.1.1.1)"));
        targets.Add(("8.8.8.8", "Internet (8.8.8.8)"));

        var stats = new List<PingStats>();
        for (var i = 0; i < targets.Count; i++)
        {
            progress?.Report(new TestProgress(100.0 * i / (targets.Count + 1), $"Pinging {targets[i].Label}..."));
            stats.Add(await PingAsync(targets[i].Address, targets[i].Label, 10, ct).ConfigureAwait(false));
        }

        progress?.Report(new TestProgress(100.0 * targets.Count / (targets.Count + 1), "Timing DNS lookups..."));
        var dnsMs = await DnsAsync(ct).ConfigureAwait(false);

        return Evaluate(stats, dnsMs);
    }

    public static TestOutcome Evaluate(IReadOnlyList<PingStats> stats, double? dnsMs)
    {
        var details = stats.Select(s => s.Received == 0
            ? $"{s.Label}: no reply ({s.Sent} sent)"
            : $"{s.Label}: {s.AverageMs:0} ms average, {s.MaxMs:0} ms max, {s.LossPercent:0}% loss").ToList();
        details.Add(dnsMs is { } d ? $"DNS lookup: {d:0} ms" : "DNS lookup: failed");

        var gateway = stats.FirstOrDefault(s => s.Label == "Gateway");
        var internet = stats.Where(s => s.Label != "Gateway").ToList();

        if (internet.All(s => s.Received == 0) && dnsMs is null)
        {
            return new(Severity.Critical, gateway is { Received: > 0 } ? "No internet: the gateway answers, but nothing beyond it" : "No network connection", details);
        }

        var problems = new List<string>();
        if (gateway is { } g && (g.LossPercent > 0 || g.AverageMs > 30))
        {
            problems.Add(g.Received == 0 ? "the gateway does not answer pings (some routers block them)" : $"the local link is unreliable ({g.LossPercent:0}% loss, {g.AverageMs:0} ms)");
        }

        if (internet.Any(s => s.LossPercent >= 5))
        {
            problems.Add("packet loss to the internet");
        }

        if (internet.Where(s => s.AverageMs is not null).Select(s => s.AverageMs!.Value).DefaultIfEmpty(0).Min() > 100)
        {
            problems.Add("high latency to the internet");
        }

        if (dnsMs > 300)
        {
            problems.Add("slow DNS");
        }

        var best = internet.Where(s => s.AverageMs is not null).OrderBy(s => s.AverageMs).FirstOrDefault();
        var summary = $"Internet {best?.AverageMs:0} ms" + (dnsMs is { } dns ? $" · DNS {dns:0} ms" : "");
        if (problems.Count > 0)
        {
            details.Insert(0, "Problems: " + string.Join("; ", problems) + ".");
            return new(Severity.Warning, summary, details);
        }

        return new(Severity.Ok, summary, details);
    }

    private static async Task<PingStats> PingAsync(string address, string label, int count, CancellationToken ct)
    {
        using var ping = new Ping();
        var times = new List<long>();
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(address, 1000).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success)
                {
                    times.Add(reply.RoundtripTime);
                }
            }
            catch (PingException)
            {
                // Counts as lost.
            }

            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        return new PingStats(address, label, count, times.Count, times.Count == 0 ? null : times.Average(), times.Count == 0 ? null : times.Max());
    }

    private static async Task<double?> DnsAsync(CancellationToken ct)
    {
        string[] names = ["www.microsoft.com", "www.office.com", "www.cloudflare.com"];
        var results = new List<double>();
        foreach (var name in names)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                await Dns.GetHostAddressesAsync(name, ct).ConfigureAwait(false);
                results.Add(watch.Elapsed.TotalMilliseconds);
            }
            catch (System.Net.Sockets.SocketException)
            {
                // Failed lookup.
            }
        }

        return results.Count == 0 ? null : results.Average();
    }
}

// ====================================================================== memory

/// <summary>Windows Memory Diagnostic: it runs at the next restart; the result appears in the System log (plan.md 5.4).</summary>
public static class MemoryDiagnostic
{
    public const string Description =
        "Opens Windows Memory Diagnostic (needs administrator rights). Choose \"Restart now and check for problems\" or \"Check for problems the next time I start my computer\". The test takes 10-20 minutes before Windows starts; Unravel shows the result after the next scan.";

    /// <summary>Starts mdsched.exe (it asks for elevation itself). False when the user cancels or it cannot start.</summary>
    public static bool Open()
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "mdsched.exe")) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

// ====================================================================== sfc / DISM

/// <summary>System File Checker and a DISM component store health check, with live output (plan.md 5.4). Admin only.</summary>
public static partial class SystemFileCheck
{
    public const string Description =
        "Runs \"sfc /scannow\" (checks and repairs protected Windows files, 5-15 minutes), then \"DISM /Online /Cleanup-Image /CheckHealth\" (a quick check of the component store). Needs administrator rights.";

    public static async Task<TestOutcome> RunAsync(IProgress<TestProgress>? progress, CancellationToken ct)
    {
        var sfcLines = new List<string>();
        progress?.Report(new TestProgress(0, "Running sfc /scannow..."));
        // sfc writes UTF-16 when its output is redirected.
        var sfcExit = await ProcessRunner.StreamAsync("sfc", "/scannow", line =>
        {
            sfcLines.Add(line);
            var percent = Percent().Match(line);
            progress?.Report(new TestProgress(percent.Success ? 0.9 * double.Parse(percent.Groups[1].Value, CultureInfo.InvariantCulture) : null,
                percent.Success ? $"sfc: {line}" : "Running sfc /scannow...", percent.Success ? null : line));
        }, Encoding.Unicode, ct).ConfigureAwait(false);

        var dismLines = new List<string>();
        progress?.Report(new TestProgress(92, "Running DISM /CheckHealth...", ""));
        var dismExit = await ProcessRunner.StreamAsync("dism", "/Online /Cleanup-Image /CheckHealth", line =>
        {
            dismLines.Add(line);
            progress?.Report(new TestProgress(null, "Running DISM /CheckHealth...", line));
        }, null, ct).ConfigureAwait(false);

        return Evaluate(sfcExit, sfcLines, dismExit, dismLines);
    }

    /// <summary>Reads the result lines (English Windows; other languages show the raw output as Info).</summary>
    public static TestOutcome Evaluate(int? sfcExit, IReadOnlyList<string> sfcLines, int? dismExit, IReadOnlyList<string> dismLines)
    {
        var sfc = string.Join(" ", sfcLines);
        var dism = string.Join(" ", dismLines);
        var details = new List<string>();
        var severity = Severity.Ok;

        void Worse(Severity s)
        {
            if (s > severity)
            {
                severity = s;
            }
        }

        if (sfcExit is null)
        {
            details.Add("sfc could not start.");
            Worse(Severity.Warning);
        }
        else if (sfc.Contains("did not find any integrity violations", StringComparison.OrdinalIgnoreCase))
        {
            details.Add("sfc: no integrity violations found.");
        }
        else if (sfc.Contains("successfully repaired", StringComparison.OrdinalIgnoreCase))
        {
            details.Add("sfc: found corrupt files and repaired them. Restart the PC.");
            Worse(Severity.Warning);
        }
        else if (sfc.Contains("unable to fix", StringComparison.OrdinalIgnoreCase))
        {
            details.Add("sfc: found corrupt files it could not repair. Run DISM /Online /Cleanup-Image /RestoreHealth, then sfc again.");
            Worse(Severity.Critical);
        }
        else if (sfc.Contains("must be an administrator", StringComparison.OrdinalIgnoreCase))
        {
            details.Add("sfc: needs administrator rights.");
            Worse(Severity.Warning);
        }
        else
        {
            details.Add($"sfc finished (exit code {sfcExit}): {sfcLines.LastOrDefault() ?? "no output"}");
            Worse(Severity.Info);
        }

        if (dismExit is null)
        {
            details.Add("DISM could not start.");
            Worse(Severity.Warning);
        }
        else if (dism.Contains("No component store corruption detected", StringComparison.OrdinalIgnoreCase))
        {
            details.Add("DISM: no component store corruption.");
        }
        else if (dism.Contains("is repairable", StringComparison.OrdinalIgnoreCase))
        {
            details.Add("DISM: the component store is damaged but repairable. Run DISM /Online /Cleanup-Image /RestoreHealth.");
            Worse(Severity.Warning);
        }
        else if (dism.Contains("not repairable", StringComparison.OrdinalIgnoreCase))
        {
            details.Add("DISM: the component store cannot be repaired. An in-place upgrade (repair install) is needed.");
            Worse(Severity.Critical);
        }
        else
        {
            details.Add($"DISM finished (exit code {dismExit}): {dismLines.LastOrDefault() ?? "no output"}");
            Worse(Severity.Info);
        }

        var summary = severity switch
        {
            Severity.Ok => "Windows system files are intact",
            Severity.Critical => "Windows system files are damaged",
            _ => "System file check finished: see details",
        };
        return new(severity, summary, details);
    }

    [GeneratedRegex(@"(\d{1,3})\s*%\s*complete", RegexOptions.IgnoreCase)]
    private static partial Regex Percent();
}

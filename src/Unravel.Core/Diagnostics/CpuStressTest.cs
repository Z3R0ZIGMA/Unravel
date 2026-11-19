using System.Diagnostics;
using System.Globalization;
using Unravel.Core.Infrastructure;

namespace Unravel.Core.Diagnostics;

/// <summary>One reading during the CPU stress test.</summary>
public sealed record CpuSample(double Seconds, double? ClockMhz, double? TemperatureC, double? LoadPercent);

/// <summary>
/// Short CPU stress test (plan.md 5.4): full load on every logical processor for 30-60 s while sampling the real clock
/// speed every second, to show throttling or a crash. Temperature only when the ACPI thermal zone gives believable
/// readings; otherwise "not available".
/// </summary>
public static class CpuStressTest
{
    public static string Describe(int seconds) =>
        $"Loads every processor core to 100% for {seconds} seconds and reads the real clock speed every second, to show throttling (the speed dropping under load) or instability. The PC gets warm and the fans may spin up; Unravel stays usable and stops the load at once if you cancel.";

    public static async Task<TestOutcome> RunAsync(int seconds, IProgress<TestProgress>? progress, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var threads = StartLoad(Environment.ProcessorCount, stop.Token);
        var samples = new List<CpuSample>();
        string? thermalNote = null;
        var watch = Stopwatch.StartNew();
        try
        {
            while (watch.Elapsed.TotalSeconds < seconds)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                var (clock, load) = ReadClock();
                var (temp, note) = ReadTemperature();
                thermalNote ??= note;
                var sample = new CpuSample(Math.Round(watch.Elapsed.TotalSeconds, 1), clock, temp, load);
                samples.Add(sample);
                progress?.Report(new TestProgress(Math.Min(100, 100 * watch.Elapsed.TotalSeconds / seconds), Status(sample, threads.Count)));
            }
        }
        finally
        {
            stop.Cancel();
            foreach (var t in threads)
            {
                t.Join(2000);
            }
        }

        return Evaluate(samples, seconds, Environment.ProcessorCount, thermalNote);
    }

    private static string Status(CpuSample s, int threads) =>
        $"Load on {threads} threads · {(s.ClockMhz is { } mhz ? Formatting.Clock((int)mhz) : "clock unknown")}"
        + (s.TemperatureC is { } t ? $" · {t:0} °C" : "") + $" · {s.Seconds:0} s";

    /// <summary>
    /// Judges the run: a clock that falls well below its early peak under sustained load is throttling
    /// (heat or power limits). Completing without a crash is itself part of the result.
    /// </summary>
    public static TestOutcome Evaluate(IReadOnlyList<CpuSample> samples, int seconds, int threads, string? thermalNote)
    {
        var clocks = samples.Where(s => s.ClockMhz is > 0).Select(s => (s.Seconds, Mhz: s.ClockMhz!.Value)).ToList();
        var details = new List<string> { $"Ran {seconds} s at full load on {threads} threads without a crash." };

        if (clocks.Count < 3)
        {
            details.Add("The clock speed could not be read, so throttling cannot be judged.");
            return new(Severity.Info, "Completed (clock speed not available)", details);
        }

        // Peak of the first quarter (turbo before heat builds up) vs the average of the last third (sustained).
        var early = clocks.Where(c => c.Seconds <= Math.Max(5, seconds / 4.0)).Select(c => c.Mhz).DefaultIfEmpty(clocks[0].Mhz).Max();
        var sustained = clocks.Where(c => c.Seconds >= seconds * 2 / 3.0).Select(c => c.Mhz).DefaultIfEmpty(clocks[^1].Mhz).Average();
        var lowest = clocks.Min(c => c.Mhz);
        var drop = early > 0 ? 100 * (early - sustained) / early : 0;

        details.Add($"Clock: {Ghz(early)} at the start, {Ghz(sustained)} sustained, lowest {Ghz(lowest)}.");
        var loads = samples.Where(s => s.LoadPercent is not null).Select(s => s.LoadPercent!.Value).ToList();
        if (loads.Count > 0)
        {
            details.Add($"Average processor load: {Math.Min(100, loads.Average()):0}%.");
        }

        var temps = samples.Where(s => s.TemperatureC is not null).Select(s => s.TemperatureC!.Value).ToList();
        details.Add(temps.Count > 0
            ? $"Temperature (ACPI thermal zone): {temps.First():0} °C at the start, {temps.Max():0} °C highest."
            : $"Temperature: not available ({thermalNote ?? "Windows does not report it"}).");

        var severity = Severity.Ok;
        string summary;
        if (drop >= 25)
        {
            severity = Severity.Warning;
            summary = $"Throttling: the clock fell {drop:0}% under load ({Ghz(early)} to {Ghz(sustained)})";
            details.Add("Check for dust, blocked vents or dried thermal paste, the power plan and the charger (laptops slow down on a weak charger or battery saver).");
        }
        else if (drop >= 10)
        {
            summary = $"Sustained {Ghz(sustained)} (turbo {Ghz(early)}): normal for this kind of PC";
            details.Add("A small drop after the first seconds is normal: turbo boost only lasts while the CPU is cool and within its power limit.");
        }
        else
        {
            summary = $"Stable at {Ghz(sustained)} under full load";
        }

        if (temps.Count > 0 && temps.Max() >= 95)
        {
            severity = Severity.Warning;
            details.Add($"It reached {temps.Max():0} °C: that is at the CPU's limit. Clean the cooling.");
        }

        return new(severity, summary, details);
    }

    private static string Ghz(double mhz) => $"{(mhz / 1000).ToString("0.00", CultureInfo.CurrentCulture)} GHz";

    private static List<Thread> StartLoad(int count, CancellationToken token)
    {
        var threads = new List<Thread>();
        for (var i = 0; i < count; i++)
        {
            var thread = new Thread(() => Spin(token))
            {
                IsBackground = true,
                // Below normal: every core is busy, but Windows and Unravel's own window still get time.
                Priority = ThreadPriority.BelowNormal,
                Name = $"Unravel CPU stress {i}",
            };
            thread.Start();
            threads.Add(thread);
        }

        return threads;
    }

    private static volatile int _sink;

    private static void Spin(CancellationToken token)
    {
        var x = 1.0001;
        while (!token.IsCancellationRequested)
        {
            for (var i = 0; i < 2_000_000; i++)
            {
                x = (x * 1.0000001) + Math.Sqrt(x) - (x / 3.0);
                if (x > 1e9 || x < 1e-9)
                {
                    x = 1.0001;
                }
            }

            _sink = (int)x;
        }
    }

    /// <summary>
    /// Actual clock = nominal frequency × % processor performance, from the processor information counters
    /// (Task Manager's "Speed"). Win32_Processor.CurrentClockSpeed barely changes, so it cannot show throttling.
    /// </summary>
    private static (double? Mhz, double? Load) ReadClock()
    {
        try
        {
            var row = Wmi.Query(WmiNamespaces.Cimv2, "Win32_PerfFormattedData_Counters_ProcessorInformation",
                ["Name", "ProcessorFrequency", "PercentProcessorPerformance", "PercentProcessorUtility"], "Name = '_Total'", TimeSpan.FromSeconds(3)).FirstOrDefault();
            var frequency = row?.Long("ProcessorFrequency");
            var performance = row?.Long("PercentProcessorPerformance");
            return (frequency is > 0 && performance is > 0 ? frequency.Value * performance.Value / 100.0 : null, row?.Long("PercentProcessorUtility"));
        }
        catch (System.Management.ManagementException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// ACPI thermal zone temperature (tenths of a kelvin). Needs admin, and many PCs return a fixed dummy value
    /// (27.8 °C is common) or nothing: only readings between 20 and 110 °C that are not that dummy count.
    /// </summary>
    private static (double? Celsius, string? Note) ReadTemperature()
    {
        try
        {
            var rows = Wmi.Query(WmiNamespaces.Wmi, "MSAcpi_ThermalZoneTemperature", ["CurrentTemperature"], null, TimeSpan.FromSeconds(3));
            var readings = rows.Select(r => r.Long("CurrentTemperature")).Where(t => t is > 0)
                .Select(t => t!.Value / 10.0 - 273.15)
                .Where(c => c is >= 20 and <= 110 && Math.Abs(c - 27.85) > 0.05)
                .ToList();
            return readings.Count == 0 ? (null, "the ACPI thermal zone gives no usable reading on this PC") : (readings.Max(), null);
        }
        catch (System.Management.ManagementException ex) when (ex.ErrorCode == System.Management.ManagementStatus.AccessDenied)
        {
            return (null, "needs administrator rights");
        }
        catch (System.Management.ManagementException)
        {
            return (null, "the ACPI thermal zone is not available on this PC");
        }
        catch (UnauthorizedAccessException)
        {
            return (null, "needs administrator rights");
        }
    }
}

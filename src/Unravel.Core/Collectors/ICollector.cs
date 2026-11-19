using System.Diagnostics;
using Unravel.Core.Infrastructure;

namespace Unravel.Core.Collectors;

/// <summary>One section of the scan (device, CPU, memory...). See plan.md section 3.</summary>
public interface ICollector
{
    string Section { get; }

    Task<CollectorResult> CollectAsync(CollectContext ctx, CancellationToken ct);
}

/// <summary>What every collector gets: where to read from, whether we are elevated, and "now" (fixed for tests).</summary>
public sealed record CollectContext(ISystemSource Source, bool IsAdmin, DateTimeOffset Now)
{
    public static CollectContext Live() => new(new LiveSystemSource(), Elevation.IsAdmin(), DateTimeOffset.Now);
}

/// <summary>
/// A collector's output. <see cref="Data"/> may be partial or null; <see cref="Warnings"/> say what could not be read
/// ("Couldn't read X (reason)"). A failing collector returns this instead of throwing.
/// </summary>
public sealed record CollectorResult
{
    public required string Section { get; init; }

    public object? Data { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Some of the data needs administrator rights; running elevated would fill it in.</summary>
    public bool NeedsAdmin { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>Nothing at all could be read for this section.</summary>
    public bool Failed => Data is null;

    public T? Get<T>() where T : class => Data as T;

    public static CollectorResult Failure(string section, string reason, TimeSpan duration, bool needsAdmin = false) => new()
    {
        Section = section,
        Warnings = [$"Couldn't read {section.ToLowerInvariant()} ({reason})"],
        NeedsAdmin = needsAdmin,
        Duration = duration,
    };
}

/// <summary>Warnings and the needs-admin flag gathered while a collector runs.</summary>
public sealed class CollectorNotes
{
    private readonly List<string> _warnings = [];

    public IReadOnlyList<string> Warnings => _warnings;

    public bool NeedsAdmin { get; private set; }

    public void Warn(string message)
    {
        if (!_warnings.Contains(message))
        {
            _warnings.Add(message);
        }
    }

    public void NeedAdmin() => NeedsAdmin = true;
}

/// <summary>
/// Base for the WMI collectors: runs the synchronous work on the thread pool, times it, and turns any exception into a
/// failed result. <see cref="TryQuery"/> reads one class and records a warning instead of throwing.
/// </summary>
public abstract class CollectorBase<T> : ICollector where T : class
{
    public abstract string Section { get; }

    public Task<CollectorResult> CollectAsync(CollectContext ctx, CancellationToken ct) => Task.Run(() =>
    {
        var watch = Stopwatch.StartNew();
        var notes = new CollectorNotes();
        try
        {
            var data = Collect(ctx, notes, ct);
            return new CollectorResult
            {
                Section = Section,
                Data = data,
                Warnings = notes.Warnings,
                NeedsAdmin = notes.NeedsAdmin && !ctx.IsAdmin,
                Duration = watch.Elapsed,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A collector must never crash the app (plan.md section 3).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var denied = ex is SystemSourceException { AccessDenied: true } || ex is UnauthorizedAccessException;
            return CollectorResult.Failure(Section, denied ? "needs administrator" : ex.Message, watch.Elapsed, denied && !ctx.IsAdmin)
                with { Warnings = [.. notes.Warnings, $"Couldn't read {Section.ToLowerInvariant()} ({(denied ? "needs administrator" : ex.Message)})"] };
        }
    }, ct);

    protected abstract T Collect(CollectContext ctx, CollectorNotes notes, CancellationToken ct);

    /// <summary>Reads a WMI class; on failure records "Couldn't read {what} (reason)" and returns an empty list.</summary>
    protected static IReadOnlyList<WmiRecord> TryQuery(
        CollectContext ctx, CollectorNotes notes, string what, string wmiNamespace, string className, string[] properties, string? where = null)
    {
        try
        {
            return ctx.Source.Query(wmiNamespace, className, properties, where);
        }
        catch (SystemSourceException ex)
        {
            if (ex.AccessDenied)
            {
                notes.NeedAdmin();
                notes.Warn($"Couldn't read {what} (needs administrator)");
            }
            else
            {
                notes.Warn($"Couldn't read {what} ({ex.Message})");
            }

            return [];
        }
    }

    /// <summary>Reads <see cref="ISystemSource.Rows"/>; on failure records a warning and returns an empty list.</summary>
    protected static IReadOnlyList<WmiRecord> TryRows(CollectContext ctx, CollectorNotes notes, string what, string kind, string? argument = null)
    {
        try
        {
            return ctx.Source.Rows(kind, argument);
        }
        catch (SystemSourceException ex)
        {
            if (ex.AccessDenied)
            {
                notes.NeedAdmin();
                notes.Warn($"Couldn't read {what} (needs administrator)");
            }
            else
            {
                notes.Warn($"Couldn't read {what} ({ex.Message})");
            }

            return [];
        }
    }

    /// <summary>A registry value as a trimmed string with OEM placeholders removed.</summary>
    protected static string? RegString(CollectContext ctx, string key, string name) =>
        ctx.Source.RegistryValue(key, name) is { } v ? OemValues.Clean(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)) : null;

    /// <summary>A registry DWORD/QWORD (or a number stored as text) as a long.</summary>
    protected static long? RegNumber(CollectContext ctx, string key, string name) => ToLong(ctx.Source.RegistryValue(key, name));

    internal static long? ToLong(object? value) => value switch
    {
        int i => i,
        long l => l,
        uint u => u,
        ulong ul when ul <= long.MaxValue => (long)ul,
        string s when long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) => n,
        byte[] { Length: 8 } b => BitConverter.ToInt64(b, 0),
        byte[] { Length: 4 } b => BitConverter.ToUInt32(b, 0),
        object[] { Length: 8 or 4 } a when a.All(x => x is long or int) => ToLong(a.Select(x => Convert.ToByte(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray()),
        _ => null,
    };
}

using Unravel.Core.Diagnostics;
using Unravel.Core.Summary;

namespace Unravel.Core.Pages;

/// <summary>
/// A page (Device, Hardware, OS, Security) as data: cards of fields, tables and status lines. The app draws every
/// page with one template; a CLI or a report could use the same models.
/// </summary>
public sealed record PageModel(string Title, string? Subtitle, IReadOnlyList<CardModel> Cards);

public sealed record CardModel
{
    public required string Title { get; init; }

    public string? Subtitle { get; init; }

    /// <summary>"Reading...", "Couldn't read" or null when the data is there.</summary>
    public string? State { get; init; }

    public IReadOnlyList<PageField> Fields { get; init; } = [];

    /// <summary>Status lines with a traffic-light glyph (problem devices, AV products...).</summary>
    public IReadOnlyList<StatusLine> Lines { get; init; } = [];

    public IReadOnlyList<TableModel> Tables { get; init; } = [];

    /// <summary>Muted notes at the bottom: what could not be read, and why.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public IReadOnlyList<PageAction> Actions { get; init; } = [];

    /// <summary>A small line chart (Reliability Monitor's index), or null.</summary>
    public ChartModel? Chart { get; init; }

    public bool HasChart => Chart is { Points.Count: > 1 };

    public bool HasFields => Fields.Count > 0;

    public bool HasLines => Lines.Count > 0;

    public bool HasTables => Tables.Count > 0;

    public bool HasNotes => Notes.Count > 0;

    public bool HasActions => Actions.Count > 0;
}

/// <summary>A label and value. <see cref="Severity"/> colours the value (Secure Boot: On in green).</summary>
/// <summary>
/// A label and value. <see cref="Severity"/> adds a status icon; hovering it shows <see cref="Hint"/> (what the value
/// means and what is good), or a general explanation of the icon when no hint is given.
/// </summary>
public sealed record PageField(string Label, string Value, bool Copyable = false, Severity? Severity = null, string? Hint = null)
{
    public bool HasSeverity => Severity is not null;

    public string? IconToolTip => Severity is null ? Hint : Hint ?? FieldHints.ForIcon(Label, Severity.Value);

    public static string GeneralMeaning(Severity severity) => severity switch
    {
        Diagnostics.Severity.Ok => "Good: nothing to do.",
        Diagnostics.Severity.Info => "For information: not a problem on its own, but worth knowing.",
        Diagnostics.Severity.Warning => "Worth a look: see Diagnostics > Health checks for what to do.",
        _ => "Needs action: see Diagnostics > Health checks for what to do.",
    };
}

/// <summary>A line in a list. With no <see cref="Severity"/> it is a plain item (no glyph), like a local admin's name.</summary>
public sealed record StatusLine(Severity? Severity, string Text, string? Detail = null)
{
    public bool HasSeverity => Severity is not null;

    public string? IconToolTip => Severity is { } s ? PageField.GeneralMeaning(s) : null;
}

public sealed record TableModel(string? Title, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, string? EmptyText = null)
{
    public bool IsEmpty => Rows.Count == 0;
}

/// <summary>Points on a fixed scale (Reliability Monitor: 1-10), with labels for the first and last day.</summary>
public sealed record ChartModel(string Title, IReadOnlyList<double> Points, double Min, double Max, string? StartLabel, string? EndLabel);

public enum PageActionKind
{
    OpenUrl,
    CopyThenOpenUrl,
    OpenUnveil,
    RestartAsAdmin,
    /// <summary>Starts a Windows tool from System32: <see cref="PageAction.Argument"/> is the exe, <see cref="PageAction.Url"/> its arguments.</summary>
    OpenWindowsTool,
}

/// <summary>A button on a card. <see cref="Argument"/> is the URL, or the text to copy for <see cref="PageActionKind.CopyThenOpenUrl"/>.</summary>
public sealed record PageAction(string Label, PageActionKind Kind, string? Argument = null, string? Url = null, string? ToolTip = null);

/// <summary>Shared pieces for page builders.</summary>
internal static class Cards
{
    public static CardModel Pending(string title, string section, ICollection<string> pending, Models.ScanResult scan)
    {
        var warnings = scan.ResultFor(section)?.Warnings ?? [];
        return new CardModel
        {
            Title = title,
            State = pending.Contains(section) ? "Reading..." : "Couldn't read this section",
            Notes = warnings,
        };
    }

    public static IReadOnlyList<string> Notes(Models.ScanResult scan, string section) => scan.ResultFor(section)?.Warnings ?? [];

    public static Severity? OnOff(bool? on, Severity off = Severity.Warning) => on switch
    {
        true => Severity.Ok,
        false => off,
        null => null,
    };

    public static string YesNo(bool? value, string yes = "Yes", string no = "No", string unknown = "Unknown") => value switch
    {
        true => yes,
        false => no,
        null => unknown,
    };

    public static TileState StateOf(object? data, string section, ICollection<string> pending) =>
        data is not null ? TileState.Ready : pending.Contains(section) ? TileState.Loading : TileState.Failed;
}

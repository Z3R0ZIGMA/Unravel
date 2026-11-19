using System.Globalization;
using System.Net;
using System.Text;
using Unravel.Core.Diagnostics;
using Unravel.Core.Models;
using Unravel.Core.Pages;
using Unravel.Core.Summary;

namespace Unravel.Core.Reports;

/// <summary>
/// The HTML report (plan.md section 6): one self-contained file (no scripts, no external files, no network), dark on
/// screen like the app and white when printed or saved as PDF. The overview and the technician's notes come first,
/// then every page of the app, drawn from the same card models the app shows.
/// </summary>
public static class HtmlReport
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    public static void Write(ReportDocument doc, string path) =>
        File.WriteAllText(path, Render(doc), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    public static string Render(ReportDocument doc)
    {
        var scan = doc.Scan;
        var none = new HashSet<string>();
        var summary = SummaryBuilder.Build(scan);
        var html = new StringBuilder(128 * 1024);

        var title = $"PC report - {summary.Title} - {Formatting.Date(doc.GeneratedAt)}";
        html.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append($"<meta name=\"generator\" content=\"{E(doc.Generator)}\">\n")
            .Append($"<title>{E(title)}</title>\n<style>{Css}</style>\n</head>\n<body>\n<main>\n");

        Header(html, doc, summary);
        Overview(html, summary);
        if (doc.Notes is { } notes)
        {
            html.Append("<section class=\"card notes-card\"><h2>Notes</h2>")
                .Append($"<div class=\"tech-notes\">{E(notes)}</div></section>\n");
        }

        Findings(html, summary.Findings);
        ActiveTests(html, doc.ActiveTests);

        Page(html, PageBuilder.Device(scan, none));
        Page(html, PageBuilder.Hardware(scan, none));
        Page(html, PageBuilder.Os(scan, none) with { Title = "Windows" });
        Page(html, PageBuilder.Security(scan, none));
        Page(html, new PageModel("Software", null, PageBuilder.SoftwareHighlights(scan, none)));
        SoftwareList(html, scan.Software);
        // The health checks are already at the top.
        Page(html, new PageModel("Diagnostics", null, DiagnosticsPage.Build(scan, none).Skip(1).ToList()));

        DataNotes(html, summary.DataNotes);
        Footer(html, doc);
        html.Append("</main>\n</body>\n</html>\n");
        return html.ToString();
    }

    // ------------------------------------------------------------------ top of the report

    private static void Header(StringBuilder html, ReportDocument doc, SummaryModel summary)
    {
        var device = doc.Scan.Device;
        html.Append("<header class=\"top\">\n<div><div class=\"brand\">Unravel</div>")
            .Append($"<div class=\"muted\">PC report &middot; {E(Formatting.DateTime(doc.GeneratedAt))}</div></div>\n")
            .Append($"<div class=\"device\"><div>{E(summary.Title)}</div>");
        var ids = new[]
        {
            device?.ComputerName is { } name ? $"Computer {name}" : null,
            device?.SerialNumber is { } serial ? $"Serial {serial}" : null,
        }.OfType<string>().ToList();
        if (ids.Count > 0)
        {
            html.Append($"<div class=\"muted\">{E(string.Join(" · ", ids))}</div>");
        }

        html.Append("</div>\n</header>\n");

        var meta = new List<string>();
        if (doc.Organisation is { } organisation)
        {
            meta.Add($"<span><span class=\"muted\">Organisation</span> {E(organisation)}</span>");
        }

        if (doc.User is { } user)
        {
            meta.Add($"<span><span class=\"muted\">User</span> {E(user)}</span>");
        }

        if (doc.Redacted)
        {
            meta.Add("<span class=\"pill\">Redacted for sharing</span>");
        }

        if (meta.Count > 0)
        {
            html.Append($"<div class=\"meta\">{string.Join("", meta)}</div>\n");
        }
    }

    private static void Overview(StringBuilder html, SummaryModel summary)
    {
        var worst = summary.Findings.Count == 0 ? Severity.Ok : summary.Findings.Max(f => f.Severity);
        var problems = summary.Findings.Count(f => f.Severity >= Severity.Warning);
        var (heading, text) = worst switch
        {
            Severity.Critical => ("Needs action", $"{Count(problems, "problem")} found. The most serious are listed first under Health checks."),
            Severity.Warning => ("Worth a look", $"{Count(problems, "thing")} to check. See Health checks below."),
            _ => ("No problems found", summary.Findings.Count == 0
                ? "Every health check passed."
                : "Nothing needs action. Health checks lists a few things worth knowing."),
        };

        html.Append($"<section class=\"verdict {Sev(worst)}\" aria-label=\"Overview\">")
            .Append($"<div class=\"verdict-icon\" aria-hidden=\"true\">{Glyph(worst)}</div>")
            .Append($"<div><h1>{E(heading)}</h1><p>{E(text)}</p></div></section>\n");

        html.Append("<section class=\"tiles\">\n");
        foreach (var tile in summary.Specs)
        {
            html.Append($"<div class=\"card tile\"><div class=\"caps\">{E(tile.Label)}</div><div class=\"tile-value\">{E(tile.Value)}</div>");
            if (tile.Detail is { } detail)
            {
                html.Append($"<div class=\"muted small\">{E(detail)}</div>");
            }

            html.Append("</div>\n");
        }

        html.Append("</section>\n<section class=\"card\"><h2>Health</h2><div class=\"badges\">\n");
        foreach (var badge in summary.Health)
        {
            var severity = badge.Severity ?? Severity.Info;
            html.Append($"<div class=\"badge {Sev(severity)}\"><span class=\"dot\" aria-hidden=\"true\">{Glyph(severity)}</span>")
                .Append($"<div><div class=\"badge-area\">{E(badge.Area)}</div><div class=\"muted small\">{E(badge.Text)}</div></div></div>\n");
        }

        html.Append("</div></section>\n");
    }

    private static void Findings(StringBuilder html, IReadOnlyList<Finding> findings)
    {
        html.Append("<section class=\"card\"><h2>Health checks</h2>");
        if (findings.Count == 0)
        {
            html.Append($"<ul class=\"lines\"><li class=\"{Sev(Severity.Ok)}\"><span class=\"dot\" aria-hidden=\"true\">{Glyph(Severity.Ok)}</span><div>No problems found</div></li></ul>");
        }
        else
        {
            html.Append("<ul class=\"lines\">");
            foreach (var f in findings)
            {
                html.Append($"<li class=\"{Sev(f.Severity)}\"><span class=\"dot\" aria-hidden=\"true\">{Glyph(f.Severity)}</span>")
                    .Append($"<div><span class=\"sr\">{SevWord(f.Severity)}: </span><strong>{E(f.Area)}:</strong> {E(f.Message)}");
                if (f.Recommendation is { } fix)
                {
                    html.Append($"<div class=\"muted small\">{E(fix)}</div>");
                }

                html.Append("</div></li>\n");
            }

            html.Append("</ul>");
        }

        html.Append("</section>\n");
    }

    private static void ActiveTests(StringBuilder html, IReadOnlyList<ActiveTestRecord> tests)
    {
        if (tests.Count == 0)
        {
            return;
        }

        html.Append("<section class=\"card\"><h2>Active tests</h2><ul class=\"lines\">");
        foreach (var t in tests)
        {
            var o = t.Outcome;
            html.Append($"<li class=\"{Sev(o.Severity)}\"><span class=\"dot\" aria-hidden=\"true\">{Glyph(o.Severity)}</span>")
                .Append($"<div><strong>{E(t.Test)}:</strong> {E(o.Summary)} <span class=\"muted small\">({E(Formatting.DateTime(t.RanAt))})</span>");
            foreach (var detail in o.Details)
            {
                html.Append($"<div class=\"muted small\">{E(detail)}</div>");
            }

            html.Append("</div></li>\n");
        }

        html.Append("</ul></section>\n");
    }

    // ------------------------------------------------------------------ the app's pages

    private static void Page(StringBuilder html, PageModel page)
    {
        html.Append($"<h2 class=\"section-title\">{E(page.Title)}</h2>\n");
        foreach (var card in page.Cards)
        {
            Card(html, card);
        }
    }

    private static void Card(StringBuilder html, CardModel card)
    {
        html.Append($"<section class=\"card\"><h3>{E(card.Title)}</h3>");
        if (card.Subtitle is { } subtitle)
        {
            html.Append($"<p class=\"muted subtitle\">{E(subtitle)}</p>");
        }

        if (card.State is { } state)
        {
            html.Append($"<p class=\"muted\">{E(state)}</p>");
        }

        if (card.HasLines)
        {
            html.Append("<ul class=\"lines\">");
            foreach (var line in card.Lines)
            {
                html.Append(line.Severity is { } s
                    ? $"<li class=\"{Sev(s)}\"><span class=\"dot\" aria-hidden=\"true\">{Glyph(s)}</span><div><span class=\"sr\">{SevWord(s)}: </span>{E(line.Text)}"
                    : $"<li class=\"plain\"><div>{E(line.Text)}");
                if (line.Detail is { } detail)
                {
                    html.Append($"<div class=\"muted small\">{E(detail)}</div>");
                }

                html.Append("</div></li>\n");
            }

            html.Append("</ul>");
        }

        if (card.HasFields)
        {
            html.Append("<dl class=\"fields\">");
            foreach (var f in card.Fields)
            {
                html.Append($"<div><dt>{E(f.Label)}</dt><dd>");
                if (f.Severity is { } s)
                {
                    html.Append($"<span class=\"{Sev(s)}\"><span class=\"dot\" aria-hidden=\"true\">{Glyph(s)}</span><span class=\"sr\">{SevWord(s)}: </span></span>");
                }

                html.Append($"<span>{E(f.Value)}</span></dd></div>\n");
            }

            html.Append("</dl>");
        }

        if (card.HasChart)
        {
            Chart(html, card.Chart!);
        }

        foreach (var table in card.Tables)
        {
            Table(html, table);
        }

        if (card.HasNotes)
        {
            html.Append("<ul class=\"notes\">");
            foreach (var note in card.Notes)
            {
                html.Append($"<li>{E(note)}</li>");
            }

            html.Append("</ul>");
        }

        html.Append("</section>\n");
    }

    private static void Table(StringBuilder html, TableModel table)
    {
        if (table.Title is { } title)
        {
            html.Append($"<h4>{E(title)}</h4>");
        }

        if (table.IsEmpty)
        {
            html.Append($"<p class=\"muted\">{E(table.EmptyText ?? "None.")}</p>");
            return;
        }

        html.Append("<div class=\"table-wrap\"><table class=\"data\"><thead><tr>");
        foreach (var column in table.Columns)
        {
            html.Append($"<th>{E(column)}</th>");
        }

        html.Append("</tr></thead><tbody>\n");
        foreach (var row in table.Rows)
        {
            html.Append("<tr>");
            foreach (var cell in row)
            {
                html.Append($"<td>{E(cell)}</td>");
            }

            html.Append("</tr>\n");
        }

        html.Append("</tbody></table></div>");
    }

    /// <summary>The Reliability Monitor line, as an inline SVG.</summary>
    private static void Chart(StringBuilder html, ChartModel chart)
    {
        const double width = 720, height = 130, left = 28, right = 8, top = 8, bottom = 22;
        var points = chart.Points;
        double X(int i) => left + (width - left - right) * i / Math.Max(1, points.Count - 1);
        double Y(double v) => top + (height - top - bottom) * (1 - (Math.Clamp(v, chart.Min, chart.Max) - chart.Min) / (chart.Max - chart.Min));
        string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

        html.Append($"<h4>{E(chart.Title)}</h4>")
            .Append($"<svg class=\"chart\" viewBox=\"0 0 {N(width)} {N(height)}\" role=\"img\" aria-label=\"{E(chart.Title)}\">");
        foreach (var tick in new[] { chart.Min, (chart.Min + chart.Max) / 2, chart.Max })
        {
            html.Append($"<line class=\"grid\" x1=\"{N(left)}\" x2=\"{N(width - right)}\" y1=\"{N(Y(tick))}\" y2=\"{N(Y(tick))}\"/>")
                .Append($"<text class=\"axis\" x=\"{N(left - 6)}\" y=\"{N(Y(tick) + 4)}\" text-anchor=\"end\">{N(tick)}</text>");
        }

        var line = string.Join(" ", points.Select((p, i) => $"{N(X(i))},{N(Y(p))}"));
        html.Append($"<polyline class=\"line\" points=\"{line}\"/>");
        if (chart.StartLabel is { } start)
        {
            html.Append($"<text class=\"axis\" x=\"{N(left)}\" y=\"{N(height - 4)}\">{E(start)}</text>");
        }

        if (chart.EndLabel is { } end)
        {
            html.Append($"<text class=\"axis\" x=\"{N(width - right)}\" y=\"{N(height - 4)}\" text-anchor=\"end\">{E(end)}</text>");
        }

        html.Append("</svg>");
    }

    /// <summary>Installed programs and Store apps, collapsed on screen (it is long) and printed in full.</summary>
    private static void SoftwareList(StringBuilder html, SoftwareInfo? software)
    {
        if (software is null)
        {
            return;
        }

        var entries = software.Visible.Concat(software.VisibleStoreApps).OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        html.Append($"<section class=\"card\"><details><summary><h3>Installed software ({entries.Count})</h3></summary>");
        Table(html, new TableModel(null, ["Name", "Version", "Publisher", "Installed", "Source"],
            entries.Select(e => (IReadOnlyList<string>)
            [
                e.Name, e.Version ?? "", e.Publisher ?? "", e.InstallDate?.ToString("d MMM yyyy", Culture) ?? "", e.Source,
            ]).ToList(), "None found."));
        html.Append("<p class=\"muted small\">System components, updates and Store framework packages are not listed.</p></details></section>\n");
    }

    private static void DataNotes(StringBuilder html, IReadOnlyList<string> notes)
    {
        if (notes.Count == 0)
        {
            return;
        }

        html.Append("<h2 class=\"section-title\">What could not be read</h2><section class=\"card\"><ul class=\"notes\">");
        foreach (var note in notes)
        {
            html.Append($"<li>{E(note)}</li>");
        }

        html.Append("</ul></section>\n");
    }

    private static void Footer(StringBuilder html, ReportDocument doc)
    {
        var scan = doc.Scan;
        html.Append("<footer class=\"muted\">")
            .Append($"<p>Made with {E(doc.Generator)} on {E(Formatting.DateTime(doc.GeneratedAt))}. ")
            .Append($"Scanned in {scan.Duration.TotalSeconds.ToString("0.0", Culture)} s ")
            .Append(scan.IsAdmin ? "as administrator." : "as a standard user, so details that need administrator rights are missing.")
            .Append("</p>");
        if (doc.Redacted)
        {
            html.Append($"<p>Redacted: {E(Redactor.Description)}</p>");
        }

        html.Append("</footer>\n");
    }

    // ------------------------------------------------------------------ helpers

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static string Sev(Severity s) => s switch
    {
        Severity.Ok => "sev-ok",
        Severity.Info => "sev-info",
        Severity.Warning => "sev-warning",
        _ => "sev-critical",
    };

    private static string Glyph(Severity s) => s switch
    {
        Severity.Ok => "&#10003;",
        Severity.Info => "i",
        Severity.Warning => "!",
        _ => "&#10005;",
    };

    private static string SevWord(Severity s) => s switch
    {
        Severity.Ok => "OK",
        Severity.Info => "Info",
        Severity.Warning => "Warning",
        _ => "Critical",
    };

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

    /// <summary>Unravel's dark palette on screen (Themes/Dark.xaml), white on paper.</summary>
    private const string Css = """

        :root {
          --window: #1e1f22; --panel: #2b2d31; --border: #3a3d43; --text: #e6e6e6; --muted: #8b8f97;
          --accent: #3b82f6; --danger: #ef4444; --success: #22c55e; --warning: #f59e0b;
          color-scheme: dark;
        }
        * { box-sizing: border-box; }
        html, body { margin: 0; background: var(--window); color: var(--text); }
        body { font: 14px/1.5 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; }
        main { max-width: 1040px; margin: 0 auto; padding: 24px 16px 48px; }
        h1, h2, h3, h4 { margin: 0; font-weight: 600; }
        h1 { font-size: 1.7em; line-height: 1.25; }
        h2 { font-size: 1.15em; margin-bottom: 10px; }
        h3 { font-size: 1.07em; }
        h4 { font-size: 1em; margin: 14px 0 6px; }
        p { margin: 4px 0 0; }
        .muted { color: var(--muted); }
        .small { font-size: .93em; }
        .sr { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; }

        .top { display: flex; justify-content: space-between; gap: 16px; flex-wrap: wrap; align-items: flex-end; margin-bottom: 12px; }
        .brand { font-size: 1.6em; font-weight: 600; }
        .device { text-align: right; font-weight: 600; font-size: 1.15em; }
        .device .muted { font-weight: 400; font-size: .87em; }
        .meta { display: flex; gap: 22px; flex-wrap: wrap; margin-bottom: 16px; }
        .pill { border: 1px solid var(--warning); color: var(--warning); border-radius: 999px; padding: 0 10px; font-size: .93em; }

        .card { background: var(--panel); border: 1px solid var(--border); border-radius: 6px; padding: 16px 18px; margin-bottom: 14px; }
        .section-title { font-size: 1.35em; margin: 30px 0 12px; }
        .subtitle { margin: 2px 0 0; }

        .verdict { display: flex; gap: 16px; align-items: center; background: var(--panel); border: 1px solid var(--border);
                   border-left: 6px solid var(--sev); border-radius: 6px; padding: 16px 20px; margin-bottom: 14px; }
        .verdict-icon { flex: none; width: 40px; height: 40px; border-radius: 50%; background: var(--sev); color: #1e1f22;
                        display: grid; place-items: center; font-size: 21px; font-weight: 700; }

        .sev-ok { --sev: var(--success); } .sev-info { --sev: var(--accent); }
        .sev-warning { --sev: var(--warning); } .sev-critical { --sev: var(--danger); }
        .dot { flex: none; display: inline-grid; place-items: center; width: 18px; height: 18px; border-radius: 50%;
               background: var(--sev); color: #1e1f22; font-size: 11px; font-weight: 700; margin-right: 8px; vertical-align: -3px; }

        .tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(180px, 100%), 1fr)); gap: 12px; margin-bottom: 14px; }
        .tiles .card { margin: 0; }
        .caps { text-transform: uppercase; letter-spacing: .06em; font-size: .8em; color: var(--muted); font-weight: 600; }
        .tile-value { font-weight: 600; margin-top: 4px; }
        .badges { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(230px, 100%), 1fr)); gap: 10px 18px; }
        .badge { display: flex; align-items: flex-start; }
        .badge .dot { margin-top: 2px; }
        .badge-area { font-weight: 600; }

        ul.lines, ul.notes { list-style: none; margin: 10px 0 0; padding: 0; }
        ul.lines li { display: flex; align-items: flex-start; margin: 6px 0; }
        ul.lines li .dot { margin-top: 1px; }
        ul.lines li.plain { padding-left: 26px; }
        ul.notes li { margin: 3px 0; font-size: .93em; color: var(--muted); }
        ul.notes li::before { content: "i"; display: inline-grid; place-items: center; width: 14px; height: 14px; border-radius: 50%;
                              border: 1px solid var(--muted); font-size: 9px; font-weight: 700; margin-right: 8px; vertical-align: 1px; }

        dl.fields { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(440px, 100%), 1fr)); gap: 0 28px; margin: 12px 0 0; }
        dl.fields > div { display: grid; grid-template-columns: 170px 1fr; gap: 12px; padding: 5px 0; border-top: 1px solid var(--border); }
        dt { color: var(--muted); }
        dd { margin: 0; display: flex; align-items: flex-start; overflow-wrap: break-word; min-width: 0; }
        dd .dot { margin-top: 1px; }
        dd > span:last-child { min-width: 0; }

        .table-wrap { overflow-x: auto; }
        table.data { width: 100%; border-collapse: collapse; font-size: .93em; margin-top: 4px; }
        table.data th { text-align: left; color: var(--muted); font-weight: 400; padding: 5px 8px; border-bottom: 1px solid var(--border); }
        table.data td { padding: 4px 8px; border-bottom: 1px solid var(--border); vertical-align: top; overflow-wrap: break-word; }

        .chart { width: 100%; max-width: 720px; height: auto; display: block; }
        .chart .grid { stroke: var(--border); stroke-width: 1; }
        .chart .axis { fill: var(--muted); font-size: 11px; font-family: inherit; }
        .chart .line { fill: none; stroke: var(--accent); stroke-width: 2; stroke-linejoin: round; stroke-linecap: round; }

        .tech-notes { white-space: pre-wrap; }
        details summary { cursor: pointer; list-style: revert; }
        details summary h3 { display: inline; }
        details[open] summary { margin-bottom: 8px; }
        footer { margin-top: 28px; font-size: .93em; }

        @media (max-width: 640px) {
          .device { text-align: left; }
          dl.fields { grid-template-columns: 1fr; }
          dl.fields > div { grid-template-columns: 130px 1fr; }
        }

        @media print {
          :root { --window: #ffffff; --panel: #ffffff; --border: #d4d7dc; --text: #111111; --muted: #555a63;
                  --accent: #2563eb; --warning: #d97706; --success: #16a34a; color-scheme: light; }
          body { font-size: 11px; }
          main { max-width: none; padding: 0; }
          .verdict, .tile, ul.lines li, dl.fields > div, tr, h4 { break-inside: avoid; }
          h3, h4 { break-after: avoid; }
          .section-title { break-after: avoid; }
          dl.fields { grid-template-columns: 1fr 1fr; }
          dl.fields > div { grid-template-columns: 36% 1fr; }
          .dot, .verdict-icon { color: #ffffff; print-color-adjust: exact; -webkit-print-color-adjust: exact; }
          details > summary { list-style: none; }
          details::details-content { content-visibility: visible; display: block; }
        }

        """;
}

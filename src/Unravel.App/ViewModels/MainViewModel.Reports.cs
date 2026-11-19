using System.IO;
using System.Windows.Input;
using Unravel.Core.Reports;

namespace Unravel.App.ViewModels;

/// <summary>
/// The Reports page (plan.md section 6): client name, ticket number and technician notes, the redaction toggle
/// (section 8), and the HTML, PDF, JSON and CSV exports. Active tests run on the Diagnostics page go in too.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly Dictionary<string, ActiveTestRecord> _testRecords = [];
    private string? _lastFolder;

    public string ClientName { get => _clientName; set => Set(ref _clientName, value); }
    private string _clientName = "";

    public string TicketNumber { get => _ticketNumber; set => Set(ref _ticketNumber, value); }
    private string _ticketNumber = "";

    public string TechnicianNotes { get => _technicianNotes; set => Set(ref _technicianNotes, value); }
    private string _technicianNotes = "";

    public bool RedactReport { get => _redactReport; set => Set(ref _redactReport, value); }
    private bool _redactReport;

    public string RedactionDescription => "Hides " + char.ToLowerInvariant(Redactor.Description[0]) + Redactor.Description[1..]
        + " The client name, ticket number and notes stay as you typed them.";

    public ICommand SaveHtmlCommand { get; private set; } = null!;

    public ICommand SavePdfCommand { get; private set; } = null!;

    public ICommand SaveJsonCommand { get; private set; } = null!;

    public ICommand SaveCsvCommand { get; private set; } = null!;

    public ICommand OpenExportCommand { get; private set; } = null!;

    public ICommand ShowExportCommand { get; private set; } = null!;

    public bool IsExporting { get => _isExporting; private set => Set(ref _isExporting, value); }
    private bool _isExporting;

    /// <summary>"Saved C:\...\report.html", or why it could not be saved.</summary>
    public string? ExportMessage { get => _exportMessage; private set => Set(ref _exportMessage, value); }
    private string? _exportMessage;

    public string? LastExportPath { get => _lastExportPath; private set { if (Set(ref _lastExportPath, value)) { OnPropertyChanged(nameof(HasLastExport)); } } }
    private string? _lastExportPath;

    public bool HasLastExport => LastExportPath is not null;

    public string ActiveTestsInReport => _testRecords.Count == 0
        ? "Active tests you run on the Diagnostics page are added to the report."
        : $"Included from the Diagnostics page: {string.Join(", ", _testRecords.Values.OrderBy(r => r.RanAt).Select(r => $"{r.Test} ({r.RanAt.ToLocalTime():HH:mm})"))}.";

    private void InitReports()
    {
        bool CanExport() => !IsScanning && !IsExporting;
        SaveHtmlCommand = new AsyncRelayCommand(() => ExportAsync("HTML report", ".html", "HTML report (*.html)|*.html",
            (doc, path) => { HtmlReport.Write(doc, path); return Task.FromResult<string?>(null); }), CanExport);
        SavePdfCommand = new AsyncRelayCommand(() => ExportAsync("PDF report", ".pdf", "PDF (*.pdf)|*.pdf",
            (doc, path) => _ui.SavePdfAsync(HtmlReport.Render(doc), path)), CanExport);
        SaveJsonCommand = new AsyncRelayCommand(() => ExportAsync("JSON data", ".json", "JSON (*.json)|*.json",
            (doc, path) => { DataExport.WriteJson(doc, path); return Task.FromResult<string?>(null); }), CanExport);
        SaveCsvCommand = new AsyncRelayCommand(() => ExportAsync("software list", ".csv", "CSV for Excel (*.csv)|*.csv",
            (doc, path) => { DataExport.WriteSoftwareCsv(doc, path); return Task.FromResult<string?>(null); }, "_software"), CanExport);
        OpenExportCommand = new RelayCommand(() => _ui.OpenFile(LastExportPath!), () => HasLastExport);
        ShowExportCommand = new RelayCommand(() => _ui.ShowInFolder(LastExportPath!), () => HasLastExport);

        // Keep the latest result of each active test for the report.
        foreach (var test in ActiveTests)
        {
            test.Completed += (sender, outcome) =>
            {
                var t = (ActiveTestViewModel)sender!;
                _testRecords[t.Title] = new ActiveTestRecord(t.Title, DateTimeOffset.Now, outcome);
                OnPropertyChanged(nameof(ActiveTestsInReport));
            };
        }
    }

    /// <param name="what">"HTML report": used in the dialog title and the messages.</param>
    /// <param name="write">Writes the file; returns an error message, or null when it worked.</param>
    private async Task ExportAsync(string what, string extension, string filter, Func<ReportDocument, string, Task<string?>> write, string suffix = "")
    {
        var doc = ReportDocument.Create(_scan, new ReportOptions
        {
            ClientName = ClientName,
            TicketNumber = TicketNumber,
            TechnicianNotes = TechnicianNotes,
            Redact = RedactReport,
        }, _testRecords.Values);

        var folder = _lastFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var path = _ui.PickSavePath($"Save {what}", folder, doc.FileStem + suffix + extension, filter);
        if (path is null)
        {
            return;
        }

        IsExporting = true;
        CommandManager.InvalidateRequerySuggested();
        ExportMessage = $"Saving the {what}...";
        try
        {
            var error = await write(doc, path);
            if (error is null)
            {
                _lastFolder = Path.GetDirectoryName(path);
                LastExportPath = path;
                ExportMessage = $"Saved the {what}{(doc.Redacted ? " (redacted)" : "")}: {path}";
                StatusText = $"Saved {Path.GetFileName(path)}.";
            }
            else
            {
                ExportMessage = error;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ExportMessage = $"Couldn't save the {what}: {ex.Message}";
        }
        finally
        {
            IsExporting = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

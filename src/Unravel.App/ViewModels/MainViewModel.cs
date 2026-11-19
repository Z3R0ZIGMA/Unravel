using System.Reflection;
using System.Windows.Input;
using Unravel.Core.Collectors;
using Unravel.Core.Infrastructure;
using Unravel.Core.Models;
using Unravel.Core.Summary;

namespace Unravel.App.ViewModels;

/// <summary>What the view model needs from the window (kept behind an interface so it holds no WPF window code).</summary>
public interface IUiServices
{
    void CopyText(string text);

    /// <summary>Restarts elevated; false when the UAC prompt was cancelled.</summary>
    bool RestartAsAdministrator();

    /// <summary>Opens a web page in the default browser.</summary>
    bool OpenUrl(string url);

    /// <summary>Starts Unveil from next to Unravel.exe, or opens its download page. Returns a status message.</summary>
    string OpenUnveil();

    /// <summary>A Save As dialog; null when cancelled.</summary>
    string? PickSavePath(string title, string folder, string fileName, string filter);

    /// <summary>Prints the HTML report to a PDF. Returns an error message, or null when it worked.</summary>
    Task<string?> SavePdfAsync(string html, string path);

    /// <summary>Opens a saved report in its default app.</summary>
    void OpenFile(string path);

    /// <summary>Opens Explorer with the file selected.</summary>
    void ShowInFolder(string path);
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly Func<ScanRunner> _createRunner;
    private readonly Func<CollectContext> _createContext;
    private readonly IUiServices _ui;
    private ScanResult _scan = new();

    public MainViewModel(Func<ScanRunner> createRunner, Func<CollectContext> createContext, IUiServices ui)
    {
        _createRunner = createRunner;
        _createContext = createContext;
        _ui = ui;
        IsAdministrator = Elevation.IsAdmin();
        _summary = SummaryBuilder.Build(_scan, createRunner().Sections);

        RescanCommand = new AsyncRelayCommand(ScanAsync, () => !IsScanning);
        CopyCommand = new ParameterCommand<string>(text =>
        {
            if (!string.IsNullOrEmpty(text))
            {
                Copy(text, $"Copied \"{text}\".");
            }
        });
        RestartAsAdminCommand = new RelayCommand(RestartAsAdmin, () => !IsAdministrator);
        RunDiagnosticsCommand = new RelayCommand(() => CurrentPage = AppPage.Diagnostics);
        InitPages();
        InitDiagnostics();
        InitReports();
        UpdatePages(createRunner().Sections);
        UpdateDiagnostics(createRunner().Sections);
    }

    /// <summary>Raised when a scan has finished (all sections done or failed).</summary>
    public event EventHandler? ScanCompleted;

    public string AppVersion { get; } = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?");

    public bool IsAdministrator { get; }

    public string AppTitle => IsAdministrator ? "Unravel (Administrator)" : "Unravel";

    public ICommand RescanCommand { get; }

    public ICommand CopyCommand { get; }

    public ICommand RestartAsAdminCommand { get; }

    public ICommand RunDiagnosticsCommand { get; }

    public SummaryModel Summary { get => _summary; private set => Set(ref _summary, value); }
    private SummaryModel _summary;

    public bool IsScanning { get => _isScanning; private set => Set(ref _isScanning, value); }
    private bool _isScanning;

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    private string _statusText = "";

    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    private double _progress;

    public bool HasFindings => Summary.Findings.Count > 0;

    public bool HasDataNotes => Summary.DataNotes.Count > 0;

    /// <summary>Some sections are incomplete without admin rights (plan.md section 7).</summary>
    public bool ShowAdminBanner => !IsAdministrator && _scan.NeedsAdmin;

    public async Task ScanAsync()
    {
        IsScanning = true;
        Progress = 0;
        var runner = _createRunner();
        var pending = new HashSet<string>(runner.Sections, StringComparer.OrdinalIgnoreCase);
        var results = new List<CollectorResult>();
        var ctx = _createContext();
        _scan = new ScanResult { StartedAt = ctx.Now, IsAdmin = ctx.IsAdmin };
        Update(pending);

        // Progress<T> was created on the UI thread, so each section fills in on the UI thread as soon as it is done.
        var progress = new Progress<CollectorResult>(result =>
        {
            results.Add(result);
            pending.Remove(result.Section);
            _scan = _scan with { Results = [.. results] };
            Progress = 100.0 * results.Count / runner.Sections.Count;
            Update(pending);
        });

        try
        {
            var final = await runner.RunAsync(ctx, progress);
            _scan = final;
            pending.Clear();
            Update(pending);
            var failed = final.Results.Count(r => r.Failed);
            StatusText = failed == 0
                ? $"Scanned in {final.Duration.TotalSeconds:0.0} s."
                : $"Scanned in {final.Duration.TotalSeconds:0.0} s; {failed} section(s) could not be read (see Data notes).";
        }
#pragma warning disable CA1031 // Never crash on a scan problem: show it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            CommandManager.InvalidateRequerySuggested();
            ScanCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Update(IReadOnlyCollection<string> pending)
    {
        Summary = SummaryBuilder.Build(_scan, pending);
        UpdatePages(pending);
        UpdateDiagnostics(pending);
        if (pending.Count > 0)
        {
            StatusText = $"Reading {string.Join(", ", pending).ToLowerInvariant()}...";
        }

        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(HasDataNotes));
        OnPropertyChanged(nameof(ShowAdminBanner));
    }

    private void Copy(string text, string status)
    {
        try
        {
            _ui.CopyText(text);
            StatusText = status;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another app is holding the clipboard.
            StatusText = "Couldn't copy: the clipboard is busy. Try again.";
        }
    }

    private void RestartAsAdmin()
    {
        if (!_ui.RestartAsAdministrator())
        {
            StatusText = "Restart as administrator was cancelled.";
        }
    }
}

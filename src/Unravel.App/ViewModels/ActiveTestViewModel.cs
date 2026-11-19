using System.Text;
using System.Windows.Input;
using Unravel.Core.Diagnostics;

namespace Unravel.App.ViewModels;

/// <summary>
/// One active test on the Diagnostics page (plan.md 5.4): it says what it will do, runs only when clicked, shows
/// progress and live output, and can be cancelled.
/// </summary>
public sealed class ActiveTestViewModel : ObservableObject
{
    private readonly Func<IProgress<TestProgress>, CancellationToken, Task<TestOutcome>> _run;
    private readonly Func<string> _describe;
    private readonly StringBuilder _log = new();
    private CancellationTokenSource? _cts;

    public ActiveTestViewModel(string title, Func<string> describe, Func<IProgress<TestProgress>, CancellationToken, Task<TestOutcome>> run,
        bool needsAdmin, bool isAdmin, bool showLog = false)
    {
        Title = title;
        _describe = describe;
        _run = run;
        NeedsAdmin = needsAdmin && !isAdmin;
        ShowLog = showLog;
        RunCommand = new AsyncRelayCommand(RunAsync, () => !IsRunning && !NeedsAdmin);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsRunning);
    }

    public string Title { get; }

    /// <summary>What the test will do, shown before it runs (it can depend on options such as the file size).</summary>
    public string Description => _describe();

    /// <summary>Needs administrator rights and the app is not elevated: Run is disabled.</summary>
    public bool NeedsAdmin { get; }

    public bool ShowLog { get; }

    public ICommand RunCommand { get; }

    public ICommand CancelCommand { get; }

    public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value); }
    private bool _isRunning;

    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    private double _progress;

    public bool IsIndeterminate { get => _indeterminate; private set => Set(ref _indeterminate, value); }
    private bool _indeterminate;

    public string? Status { get => _status; private set => Set(ref _status, value); }
    private string? _status;

    public TestOutcome? Outcome { get => _outcome; private set => Set(ref _outcome, value); }
    private TestOutcome? _outcome;

    public string Log { get => _logText; private set => Set(ref _logText, value); }
    private string _logText = "";

    public bool HasLog => ShowLog && Log.Length > 0;

    /// <summary>Raised when the test finishes, so the page can rescan (a memory or sfc result changes the findings).</summary>
    public event EventHandler<TestOutcome>? Completed;

    public void RefreshDescription() => OnPropertyChanged(nameof(Description));

    private async Task RunAsync()
    {
        _cts = new CancellationTokenSource();
        IsRunning = true;
        Outcome = null;
        _log.Clear();
        Log = "";
        Progress = 0;
        IsIndeterminate = true;
        Status = "Starting...";
        CommandManager.InvalidateRequerySuggested();

        // Created on the UI thread: reports arrive on the UI thread.
        var progress = new Progress<TestProgress>(p =>
        {
            if (p.Percent is { } percent)
            {
                IsIndeterminate = false;
                Progress = percent;
            }

            Status = p.Status;
            if (p.OutputLine is { } line && ShowLog)
            {
                _log.AppendLine(line);
                Log = _log.ToString();
                OnPropertyChanged(nameof(HasLog));
            }
        });

        try
        {
            Outcome = await _run(progress, _cts.Token);
            Status = null;
            Completed?.Invoke(this, Outcome);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
#pragma warning disable CA1031 // A test that fails must not crash the app: show why.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Outcome = new TestOutcome(Severity.Warning, "The test could not finish", [ex.Message]);
            Status = null;
        }
        finally
        {
            IsRunning = false;
            IsIndeterminate = false;
            _cts.Dispose();
            _cts = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

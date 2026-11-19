using System.IO;
using System.Windows.Input;
using Unravel.Core.Diagnostics;
using Unravel.Core.Pages;

namespace Unravel.App.ViewModels;

/// <summary>The Diagnostics page: the report cards from Core plus the active tests (plan.md section 5).</summary>
public sealed partial class MainViewModel
{
    public IReadOnlyList<CardModel> DiagnosticsCards { get => _diagnosticsCards; private set => Set(ref _diagnosticsCards, value); }
    private IReadOnlyList<CardModel> _diagnosticsCards = [];

    public IReadOnlyList<ActiveTestViewModel> ActiveTests { get; private set; } = [];

    public ActiveTestViewModel DiskTest { get; private set; } = null!;

    public ActiveTestViewModel NetworkTest { get; private set; } = null!;

    public ActiveTestViewModel SystemFilesTest { get; private set; } = null!;

    public ActiveTestViewModel CpuTest { get; private set; } = null!;

    public int[] CpuTestDurations { get; } = [30, 60];

    public int CpuTestSeconds
    {
        get => _cpuTestSeconds;
        set
        {
            if (Set(ref _cpuTestSeconds, value))
            {
                CpuTest.RefreshDescription();
            }
        }
    }
    private int _cpuTestSeconds = 30;

    public int[] DiskTestSizes { get; } = [256, 1024, 4096];

    public int DiskTestSizeMb
    {
        get => _diskTestSizeMb;
        set
        {
            if (Set(ref _diskTestSizeMb, value))
            {
                DiskTest.RefreshDescription();
            }
        }
    }
    private int _diskTestSizeMb = 1024;

    public string MemoryTestDescription => MemoryDiagnostic.Description;

    public string MemoryTestLastResult => _scan.Stability?.LastMemoryTest is { } m
        ? $"Last result ({Core.Formatting.Date(m.Time)}): {(m.ErrorsFound ? "errors found" : "no errors")}"
        : "No earlier result in the event log.";

    public ICommand OpenMemoryDiagnosticCommand { get; private set; } = null!;

    private void InitDiagnostics()
    {
        var temp = Path.GetTempPath();
        DiskTest = new ActiveTestViewModel("Disk speed",
            () => DiskSpeedTest.Describe(DiskTestSizeMb, temp),
            (p, ct) => DiskSpeedTest.RunAsync(DiskTestSizeMb, temp, _scan.Storage?.SystemDisk?.Kind, p, ct),
            needsAdmin: false, IsAdministrator);

        NetworkTest = new ActiveTestViewModel("Network",
            () => Core.Diagnostics.NetworkTest.Describe(Gateway()),
            (p, ct) => Core.Diagnostics.NetworkTest.RunAsync(Gateway(), p, ct),
            needsAdmin: false, IsAdministrator);

        SystemFilesTest = new ActiveTestViewModel("System files (sfc and DISM)",
            () => SystemFileCheck.Description,
            (p, ct) => SystemFileCheck.RunAsync(p, ct),
            needsAdmin: true, IsAdministrator, showLog: true);

        CpuTest = new ActiveTestViewModel("CPU stress",
            () => CpuStressTest.Describe(CpuTestSeconds),
            (p, ct) => CpuStressTest.RunAsync(CpuTestSeconds, p, ct),
            needsAdmin: false, IsAdministrator);

        ActiveTests = [DiskTest, NetworkTest, CpuTest, SystemFilesTest];

        OpenMemoryDiagnosticCommand = new RelayCommand(() =>
            StatusText = MemoryDiagnostic.Open()
                ? "Windows Memory Diagnostic opened: choose when to restart and test."
                : "Windows Memory Diagnostic was cancelled.");
    }

    private string? Gateway() =>
        _scan.Network?.Adapters.Where(a => a.Connected && !a.IsVirtual).SelectMany(a => a.Gateways)
            .FirstOrDefault(g => System.Net.IPAddress.TryParse(g, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

    private void UpdateDiagnostics(IReadOnlyCollection<string> pending)
    {
        DiagnosticsCards = DiagnosticsPage.Build(_scan, new HashSet<string>(pending, StringComparer.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(MemoryTestLastResult));
        NetworkTest.RefreshDescription();
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using Unravel.Core.Models;
using Unravel.Core.Pages;

namespace Unravel.App.ViewModels;

public enum AppPage
{
    Summary,
    Device,
    Hardware,
    Os,
    Software,
    Security,
}

/// <summary>The sidebar pages: Device, Hardware, OS and Security are built in Core; Software adds a searchable grid.</summary>
public sealed partial class MainViewModel
{
    private readonly ObservableCollection<SoftwareEntry> _software = [];
    private ICollectionView? _softwareView;

    public AppPage CurrentPage { get => _currentPage; set => Set(ref _currentPage, value); }
    private AppPage _currentPage = AppPage.Summary;

    public PageModel? DevicePage { get => _devicePage; private set => Set(ref _devicePage, value); }
    private PageModel? _devicePage;

    public PageModel? HardwarePage { get => _hardwarePage; private set => Set(ref _hardwarePage, value); }
    private PageModel? _hardwarePage;

    public PageModel? OsPage { get => _osPage; private set => Set(ref _osPage, value); }
    private PageModel? _osPage;

    public PageModel? SecurityPage { get => _securityPage; private set => Set(ref _securityPage, value); }
    private PageModel? _securityPage;

    public IReadOnlyList<CardModel> SoftwareHighlights { get => _softwareHighlights; private set => Set(ref _softwareHighlights, value); }
    private IReadOnlyList<CardModel> _softwareHighlights = [];

    /// <summary>Installed programs and Store apps, filtered by the search box and the toggles.</summary>
    public ICollectionView SoftwareView => _softwareView ??= CreateSoftwareView();

    public string SoftwareSearch
    {
        get => _softwareSearch;
        set
        {
            if (Set(ref _softwareSearch, value))
            {
                RefreshSoftware();
            }
        }
    }
    private string _softwareSearch = "";

    public bool ShowStoreApps { get => _showStoreApps; set { if (Set(ref _showStoreApps, value)) { RefreshSoftware(); } } }
    private bool _showStoreApps = true;

    /// <summary>System components, updates and patches (hidden in Programs and Features too).</summary>
    public bool ShowHiddenEntries { get => _showHidden; set { if (Set(ref _showHidden, value)) { RefreshSoftware(); } } }
    private bool _showHidden;

    /// <summary>Store framework packages and Windows' own inbox apps.</summary>
    public bool ShowSystemPackages { get => _showSystem; set { if (Set(ref _showSystem, value)) { RefreshSoftware(); } } }
    private bool _showSystem;

    public string SoftwareCount { get => _softwareCount; private set => Set(ref _softwareCount, value); }
    private string _softwareCount = "";

    public ICommand PageActionCommand { get; private set; } = null!;

    private void InitPages()
    {
        PageActionCommand = new ParameterCommand<PageAction>(RunPageAction);
    }

    private void UpdatePages(IReadOnlyCollection<string> pending)
    {
        var set = new HashSet<string>(pending, StringComparer.OrdinalIgnoreCase);
        DevicePage = PageBuilder.Device(_scan, set);
        HardwarePage = PageBuilder.Hardware(_scan, set);
        OsPage = PageBuilder.Os(_scan, set);
        SecurityPage = PageBuilder.Security(_scan, set);
        SoftwareHighlights = PageBuilder.SoftwareHighlights(_scan, set);

        var software = _scan.Software;
        var entries = software is null ? [] : software.Programs.Concat(software.StoreApps).ToList();
        if (entries.Count != _software.Count || (software is not null && !ReferenceEquals(_lastSoftware, software)))
        {
            _lastSoftware = software;
            _software.Clear();
            foreach (var e in entries)
            {
                _software.Add(e);
            }

            RefreshSoftware();
        }
    }

    private SoftwareInfo? _lastSoftware;

    private ICollectionView CreateSoftwareView()
    {
        var view = CollectionViewSource.GetDefaultView(_software);
        view.Filter = o => o is SoftwareEntry e && Matches(e);
        view.SortDescriptions.Add(new SortDescription(nameof(SoftwareEntry.Name), ListSortDirection.Ascending));
        return view;
    }

    private bool Matches(SoftwareEntry e)
    {
        if (e.Source == "Store" && (!ShowStoreApps || (e.IsSystemPackage && !ShowSystemPackages)))
        {
            return false;
        }

        if (e.IsHidden && !ShowHiddenEntries)
        {
            return false;
        }

        var q = SoftwareSearch.Trim();
        return q.Length == 0
               || e.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
               || e.Publisher?.Contains(q, StringComparison.CurrentCultureIgnoreCase) == true
               || e.Version?.Contains(q, StringComparison.CurrentCultureIgnoreCase) == true;
    }

    private void RefreshSoftware()
    {
        SoftwareView.Refresh();
        var shown = _software.Count(Matches);
        SoftwareCount = $"{shown} of {_software.Count} shown";
    }

    private void RunPageAction(PageAction? action)
    {
        if (action is null)
        {
            return;
        }

        switch (action.Kind)
        {
            case PageActionKind.OpenUrl when action.Argument is { } url:
                StatusText = _ui.OpenUrl(url) ? "Opened in your browser." : "Couldn't open the browser.";
                break;
            case PageActionKind.CopyThenOpenUrl when action.Argument is { } text && action.Url is { } url:
                Copy(text, $"Copied \"{text}\": paste it into the page.");
                _ui.OpenUrl(url);
                break;
            case PageActionKind.OpenUnveil:
                StatusText = _ui.OpenUnveil();
                break;
            case PageActionKind.RestartAsAdmin:
                RestartAsAdmin();
                break;
        }
    }
}

using System.Windows;
using Unravel.App.Native;
using Unravel.App.ViewModels;
using Unravel.Core.Infrastructure;

namespace Unravel.App;

public partial class MainWindow : Window, IUiServices
{
    public MainWindow()
    {
        InitializeComponent();
        FitToScreen();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
    }

    /// <summary>The scrollable page at its full height, for the screenshot mode.</summary>
    public FrameworkElement RootContent => Page;

    /// <summary>Keeps the whole window on screen on small laptops at high scaling (same as Unveil).</summary>
    private void FitToScreen()
    {
        const double margin = 24;
        var area = SystemParameters.WorkArea;

        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Min(Width, area.Width - margin);
        Height = Math.Min(Height, area.Height - margin);

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    public void CopyText(string text) => Clipboard.SetText(text);

    public bool RestartAsAdministrator()
    {
        if (!Elevation.RelaunchAsAdmin(Environment.GetCommandLineArgs().Skip(1)))
        {
            return false;
        }

        Application.Current.Shutdown();
        return true;
    }
}

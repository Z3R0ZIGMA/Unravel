using System.Windows.Controls;

namespace Unravel.App.Views;

/// <summary>The Diagnostics page: report cards and active tests. Bound to <see cref="ViewModels.MainViewModel"/>.</summary>
public partial class DiagnosticsView : UserControl
{
    public DiagnosticsView() => InitializeComponent();

    /// <summary>Keeps the live sfc / DISM output scrolled to the newest line.</summary>
    private void ScrollLogToEnd(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.ScrollToEnd();
        }
    }
}

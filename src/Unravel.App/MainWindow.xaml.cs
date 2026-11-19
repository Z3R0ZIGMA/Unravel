using System.Diagnostics;
using System.IO;
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

        // A new page starts at the top.
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is MainViewModel vm)
            {
                vm.PropertyChanged += (_, p) =>
                {
                    if (p.PropertyName == nameof(MainViewModel.CurrentPage))
                    {
                        Scroller.ScrollToTop();
                    }
                };
            }
        };
    }

    /// <summary>The scrollable page at its full height, for the screenshot mode.</summary>
    public FrameworkElement RootContent => Page;

    /// <summary>The visible window content (sidebar included), for the screenshot mode.</summary>
    public FrameworkElement WindowContent => Root;

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

    public bool OpenUrl(string url)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Unveil.exe (or Unveil-arm64.exe) next to Unravel.exe, as on a technician's USB stick; else its releases page.</summary>
    public string OpenUnveil()
    {
        var folder = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        foreach (var name in new[] { "Unveil.exe", "Unveil-arm64.exe" })
        {
            var exe = Path.Combine(folder, name);
            if (File.Exists(exe))
            {
                try
                {
                    using var _ = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = folder });
                    return "Started Unveil.";
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    return $"Couldn't start Unveil: {ex.Message}";
                }
            }
        }

        return OpenUrl("https://github.com/Z3R0ZIGMA/Unveil/releases/latest")
            ? "Unveil.exe is not next to Unravel.exe: opened its download page."
            : "Unveil.exe is not next to Unravel.exe.";
    }

    public string? PickSavePath(string title, string folder, string fileName, string filter)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            InitialDirectory = folder,
            FileName = fileName,
            Filter = filter,
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    public async Task<string?> SavePdfAsync(string html, string path)
    {
        try
        {
            await Reports.PdfExport.SaveAsync(html, path, new System.Windows.Interop.WindowInteropHelper(this).Handle);
            return null;
        }
        catch (Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
        {
            return Reports.PdfExport.RuntimeMissing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return $"Couldn't save the PDF: {ex.Message}";
        }
    }

    public void OpenFile(string path) => OpenUrl(path);

    public void ShowInFolder(string path)
    {
        try
        {
            // explorer.exe from the Windows folder, with the file selected.
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var _ = Process.Start(new ProcessStartInfo(explorer, $"/select,\"{path}\"") { UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Nothing useful to do: the path is shown on the page.
        }
    }

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

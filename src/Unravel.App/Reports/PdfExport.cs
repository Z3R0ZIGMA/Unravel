using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace Unravel.App.Reports;

/// <summary>
/// Saves the HTML report as a PDF (plan.md section 2): the Edge WebView2 runtime, part of Windows 11 and installed
/// with Edge on Windows 10, loads the report out of sight and prints it with the report's white print styles.
/// </summary>
public static class PdfExport
{
    /// <summary>Shown when the WebView2 runtime is missing.</summary>
    public const string RuntimeMissing =
        "PDF needs the Microsoft Edge WebView2 Runtime, which is missing on this PC. Save the HTML report and print it to PDF from a browser instead.";

    /// <param name="html">The report.</param>
    /// <param name="path">Where to write the PDF.</param>
    /// <param name="window">A window handle to host the (hidden) browser.</param>
    /// <exception cref="WebView2RuntimeNotFoundException">No WebView2 runtime.</exception>
    /// <exception cref="IOException">The PDF could not be written.</exception>
    public static async Task SaveAsync(string html, string path, IntPtr window)
    {
        // Its profile goes to %TEMP%, not next to Unravel.exe (which may be on a read-only USB stick).
        var folder = Path.Combine(Path.GetTempPath(), "Unravel", "WebView2");
        var options = new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = false };
        var environment = await CoreWebView2Environment.CreateAsync(null, folder, options);
        var controller = await environment.CreateCoreWebView2ControllerAsync(window);
        string? tempFile = null;
        try
        {
            controller.IsVisible = false;
            var web = controller.CoreWebView2;
            web.Settings.IsScriptEnabled = false;
            web.Settings.AreDefaultContextMenusEnabled = false;
            // The report is local and trusted: no SmartScreen look-up (which would send its address to Microsoft).
            web.Settings.IsReputationCheckingRequired = false;

            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            web.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);

            // NavigateToString is limited to 2 MB; a long software list can pass that, so large reports go via a file.
            if (Encoding.UTF8.GetByteCount(html) < 1_500_000)
            {
                web.NavigateToString(html);
            }
            else
            {
                tempFile = Path.Combine(folder, $"report-{Guid.NewGuid():N}.html");
                await File.WriteAllTextAsync(tempFile, html, new UTF8Encoding(false));
                web.Navigate(new Uri(tempFile).AbsoluteUri);
            }

            if (!await loaded.Task)
            {
                throw new IOException("The report could not be loaded for printing.");
            }

            var settings = environment.CreatePrintSettings();
            settings.ShouldPrintHeaderAndFooter = false;
            settings.ShouldPrintBackgrounds = true;
            settings.MarginTop = settings.MarginBottom = 0.5;
            settings.MarginLeft = settings.MarginRight = 0.5;
            // A4 where the metric system is used, US Letter elsewhere (inches).
            if (RegionInfo.CurrentRegion.IsMetric)
            {
                settings.PageWidth = 8.27;
                settings.PageHeight = 11.69;
            }

            if (!await web.PrintToPdfAsync(path, settings))
            {
                throw new IOException("WebView2 could not write the PDF.");
            }
        }
        finally
        {
            controller.Close();
            if (tempFile is not null)
            {
                try
                {
                    File.Delete(tempFile);
                }
                catch (IOException)
                {
                    // Left in %TEMP%: not worth failing the export over.
                }
            }
        }
    }
}

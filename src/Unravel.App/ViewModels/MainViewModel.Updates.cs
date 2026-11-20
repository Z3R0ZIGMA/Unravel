using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows.Input;
using Unravel.Core.Updates;

namespace Unravel.App.ViewModels;

/// <summary>
/// "Check for updates" (top bar): looks for a newer release on GitHub only when clicked, asks before downloading,
/// checks the download against SHA256SUMS.txt, swaps it in for the running Unravel.exe and offers to restart.
/// </summary>
public sealed partial class MainViewModel
{
    public ICommand CheckForUpdatesCommand { get; private set; } = null!;

    public bool IsUpdating { get => _isUpdating; private set => Set(ref _isUpdating, value); }
    private bool _isUpdating;

    private static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>The single-file release exe stands alone; a development build (dotnet run) has Unravel.dll next to it.</summary>
    private static bool IsReleaseBuild =>
        Environment.ProcessPath is { } exe && !File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "Unravel.dll"));

    private void InitUpdates()
    {
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync, () => !IsUpdating);
    }

    private async Task CheckForUpdatesAsync()
    {
        IsUpdating = true;
        CommandManager.InvalidateRequerySuggested();
        var current = CurrentVersion;
        StatusText = "Checking GitHub for a newer version...";
        try
        {
            using var http = AppUpdate.CreateClient(current);
            var latest = await AppUpdate.GetLatestAsync(http, CancellationToken.None);
            if (latest is null)
            {
                StatusText = "Couldn't find a release to update to.";
                return;
            }

            if (!AppUpdate.IsNewer(latest.Version, current))
            {
                StatusText = $"Unravel is up to date ({current.ToString(3)}).";
                return;
            }

            var newVersion = latest.Version.ToString(3);
            if (!IsReleaseBuild || Environment.ProcessPath is not { } exe)
            {
                StatusText = $"Unravel {newVersion} is available. This is a development build: get it from the releases page.";
                _ui.OpenUrl(latest.PageUrl);
                return;
            }

            if (!_ui.Confirm($"Unravel {newVersion} is available (you have {current.ToString(3)}).\n\n" +
                             "Download it and update now? It is checked against the release's checksums before it is used.",
                             "Update available"))
            {
                StatusText = $"Unravel {newVersion} is available: click Check for updates when you're ready.";
                return;
            }

            // Next to the running exe when that folder can be written to, so it can replace it; else in Downloads.
            var folder = Path.GetDirectoryName(exe)!;
            var staged = exe + ".new";
            var progress = new Progress<double>(p => StatusText = $"Downloading Unravel {newVersion}... {p:0}%");
            try
            {
                await AppUpdate.DownloadAsync(http, latest, staged, progress, CancellationToken.None);
                AppUpdate.Install(staged, exe);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && ex is not InvalidDataException)
            {
                // Read-only folder (a locked USB stick, Program Files): save it where the user can get it.
                TryDelete(staged);
                var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                var saved = Path.Combine(downloads, $"Unravel-{newVersion}{(latest.AssetName.Contains("arm64", StringComparison.Ordinal) ? "-arm64" : "")}.exe");
                await AppUpdate.DownloadAsync(http, latest, saved, progress, CancellationToken.None);
                StatusText = $"Couldn't replace Unravel.exe in {folder}: Unravel {newVersion} was saved to {saved}.";
                _ui.ShowInFolder(saved);
                return;
            }

            if (_ui.Confirm($"Unravel was updated to {newVersion}. Restart it now?", "Update installed"))
            {
                _ui.RestartInto(exe);
                return;
            }

            StatusText = $"Updated to {newVersion}: it starts the next time you open Unravel.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException
                                       or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            StatusText = $"Couldn't update: {ex.Message}";
        }
        finally
        {
            IsUpdating = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for AppUpdate.CleanUpOldVersion on the next start.
        }
    }
}

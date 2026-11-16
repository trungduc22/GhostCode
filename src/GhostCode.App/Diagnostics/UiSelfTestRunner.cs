using System.Text;
using System.Text.Json;
using GhostCode.App.ViewModels;
using GhostCode.App.Views;
using GhostCode.Core.Archiver;
using GhostCode.Core.Cloud;
using GhostCode.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GhostCode.App.Diagnostics;

/// <summary>
/// Executes live UI-thread verification across all WinUI 3 pages, controls, commands,
/// theme/language toggles, integrity handling, and System Tray hide/restore when
/// GHOSTCODE_SELF_TEST_REPORT is specified.
/// </summary>
public static class UiSelfTestRunner
{
    public static async Task RunAndWriteReportAsync(
        string reportPath,
        MainWindow window,
        MainViewModel vm,
        GhostCodeConfig config,
        string configPath,
        DpapiCredentialStore credStore,
        string testWorkspaceRoot)
    {
        var results = new Dictionary<string, object>();
        bool allPassed = true;

        void Record(string name, bool passed, string detail = "")
        {
            results[name] = new { passed, detail };
            if (!passed)
                allPassed = false;
        }

        try
        {
            await Task.Delay(300);

            Record("MicaBackdropAttached", window.SystemBackdrop is MicaBackdrop, window.SystemBackdrop?.GetType().Name ?? "null");

            // 1. Navigate across all 4 NavigationView pages
            var navOk = window.NavigateToTag("Archive") && window.CurrentPageType == typeof(ArchivePage);
            await Task.Delay(120);
            navOk &= window.NavigateToTag("Cloud") && window.CurrentPageType == typeof(CloudPage);
            await Task.Delay(120);
            navOk &= window.NavigateToTag("Settings") && window.CurrentPageType == typeof(SettingsPage);
            await Task.Delay(120);
            navOk &= window.NavigateToTag("Dashboard") && window.CurrentPageType == typeof(DashboardPage);
            Record("NavigationViewPages", navOk, window.CurrentPageType?.Name ?? "null");

            // 2. Settings: add folder, toggle recursive, refuse ArchiveDir, remove folder
            var tempWatchDir = Path.Combine(testWorkspaceRoot, "SelfTestWatched");
            Directory.CreateDirectory(tempWatchDir);
            vm.AddWatchedFolder(tempWatchDir);
            var addedItem = vm.WatchedFolders.FirstOrDefault(w => string.Equals(w.Path, tempWatchDir, StringComparison.OrdinalIgnoreCase));
            bool folderAddOk = addedItem is not null;

            bool recursiveToggleOk = false;
            if (addedItem is not null)
            {
                addedItem.IsRecursive = false;
                addedItem.IsRecursive = true;
                recursiveToggleOk = config.WatchedFolders.Any(w => string.Equals(w.Path, tempWatchDir, StringComparison.OrdinalIgnoreCase) && w.Recursive);
            }

            vm.AddWatchedFolder(config.ArchiveDir);
            bool excludedGuardOk = vm.InfoBarSeverity == InfoBarSeverity.Error;

            // Keep tempWatchDir temporarily for archive test below, remove after

            // 3. Language & Theme switching
            vm.SelectedThemeIndex = 1; // Dark
            bool darkOk = window.CurrentRootTheme == ElementTheme.Dark;
            vm.SelectedThemeIndex = 2; // Light
            bool lightOk = window.CurrentRootTheme == ElementTheme.Light;
            vm.SelectedThemeIndex = 0; // System
            bool themeOk = darkOk && lightOk && window.CurrentRootTheme == ElementTheme.Default;
            Record("LightDarkThemeSwitch", themeOk);

            vm.SelectedLanguageIndex = 1; // English
            bool enOk = vm.Strings.NavDashboard == "Dashboard";
            vm.SelectedLanguageIndex = 0; // Vietnamese
            bool viOk = vm.Strings.NavDashboard == "Tổng quan";
            Record("BilingualSwitch", enOk && viOk);

            // 4. Archive & Ready path updates
            var newReady = Path.Combine(testWorkspaceRoot, "SelfTestReady");
            Directory.CreateDirectory(newReady);
            vm.SetReadyDirectory(newReady);
            bool pathsOk = string.Equals(vm.ReadyDir, newReady, StringComparison.OrdinalIgnoreCase);
            Record("ArchiveAndReadyPathControls", pathsOk && folderAddOk && recursiveToggleOk && excludedGuardOk);

            // 5. Diagnostic button
            await vm.RunDiagnosticCommand.ExecuteAsync(null);
            Record("DiagnosticButton", vm.LastDiagnosticResultText.Contains("PASS", StringComparison.Ordinal), vm.LastDiagnosticResultText);

            // 6. Save a file -> monitor archives -> SearchArchive -> Preview -> CopyCode -> RestoreVersion -> Corrupt Integrity Fail
            window.NavigateToTag("Archive");
            var srcFile = Path.Combine(tempWatchDir, "selftest_sol.cpp");
            const string srcCode = "#include <iostream>\nint main(){ std::cout << \"WINUI_SELFTEST\"; return 0; }\n";
            await File.WriteAllBytesAsync(srcFile, Encoding.UTF8.GetBytes(srcCode));

            var deadline = Environment.TickCount64 + 6000;
            while (Environment.TickCount64 < deadline)
            {
                await vm.RefreshAllAsync();
                if (vm.ArchiveVersions.Count > 0)
                    break;
                await Task.Delay(120);
            }

            vm.SearchQuery = "selftest_sol";
            await vm.SearchArchiveCommand.ExecuteAsync(null);
            bool searchOk = vm.ArchiveVersions.Count >= 1;
            Record("ArchiveSearchAndVersions", searchOk, $"count={vm.ArchiveVersions.Count}");

            if (searchOk)
            {
                vm.SelectedVersion = vm.ArchiveVersions[0];
                await Task.Delay(250);
                bool previewOk = vm.SelectedVersionSource.Contains("WINUI_SELFTEST", StringComparison.Ordinal);
                Record("SourcePreviewAndDiagnostics", previewOk && vm.LastDetectedFileText.Contains("selftest_sol.cpp", StringComparison.Ordinal), vm.LastDetectedFileText);

                vm.CopySelectedCodeCommand.Execute(null);
                Record("CopyCodeCommand", vm.InfoBarSeverity == InfoBarSeverity.Success);

                var restoredPath = Path.Combine(testWorkspaceRoot, "restored_selftest.cpp");
                bool restoreOk = await vm.RestoreSelectedVersionAsync(restoredPath)
                    && File.Exists(restoredPath)
                    && (await File.ReadAllTextAsync(restoredPath)).Contains("WINUI_SELFTEST", StringComparison.Ordinal);

                // Tamper archive file and verify restore refuses with InfoBar Error without crashing
                var verArchivePath = vm.SelectedVersion.ArchivePath;
                await File.WriteAllTextAsync(verArchivePath, "// CORRUPTED");
                bool corruptBlocked = !(await vm.RestoreSelectedVersionAsync(Path.Combine(testWorkspaceRoot, "bad_restore.cpp")))
                    && vm.InfoBarSeverity == InfoBarSeverity.Error;

                Record("RestoreAndIntegrityCheck", restoreOk && corruptBlocked);
            }
            else
            {
                Record("SourcePreviewAndDiagnostics", false, "No version archived");
                Record("CopyCodeCommand", false, "Skipped");
                Record("RestoreAndIntegrityCheck", false, "Skipped");
            }

            if (addedItem is not null)
            {
                vm.RemoveWatchedFolder(addedItem);
                Record("RemoveWatchedFolder", !vm.WatchedFolders.Contains(addedItem));
            }
            else
            {
                Record("RemoveWatchedFolder", false);
            }

            // 7. Cloud controls (Configure OAuth -> DPAPI secret -> Connect/Sync/Retry/Disconnect)
            window.NavigateToTag("Cloud");
            await vm.ConnectGoogleCommand.ExecuteAsync(null);
            bool connectGuardOk = vm.InfoBarSeverity == InfoBarSeverity.Warning;

            await vm.SaveOAuthClientAsync("self-test-client.apps.googleusercontent.com", "secret-never-in-config");
            var rawCfg = await File.ReadAllTextAsync(configPath);
            bool dpapiSecretOk = !rawCfg.Contains("secret-never-in-config", StringComparison.Ordinal)
                && File.Exists(credStore.ClientSecretFilePath)
                && await credStore.LoadClientSecretAsync() == "secret-never-in-config";

            await vm.SyncNowCommand.ExecuteAsync(null);
            await vm.RetryFailedCommand.ExecuteAsync(null);
            await vm.DisconnectGoogleCommand.ExecuteAsync(null);
            Record("CloudControlsAndDpapi", connectGuardOk && dpapiSecretOk && !vm.IsDriveConnected);

            // 8. System Tray hide / restore
            bool taskbarPresent = GhostCode.App.Interop.TrayIconManager.IsTaskbarPresent;
            bool iconLoaded = window.TrayManager?.HasValidIcon ?? false;
            bool trayRegOk = !taskbarPresent || (window.TrayManager?.IsIconAdded ?? false);
            bool trayHideOk = false, trayRestoreOk = false;
            if (window.TrayManager is not null && vm.Config.MinimizeToTrayOnClose)
            {
                window.TrayManager.HideToTray();
                await Task.Delay(80);
                trayHideOk = !window.TrayManager.AppWindow.IsVisible;
                window.TrayManager.ShowAndActivateWindow();
                await Task.Delay(80);
                trayRestoreOk = window.TrayManager.AppWindow.IsVisible;
            }
            Record("SystemTrayAndHideRestore", iconLoaded && trayRegOk && trayHideOk && trayRestoreOk,
                $"iconLoaded={iconLoaded}, taskbarPresent={taskbarPresent}, trayAdded={window.TrayManager?.IsIconAdded}, hide={trayHideOk}, restore={trayRestoreOk}");
        }
        catch (Exception ex)
        {
            allPassed = false;
            results["UnhandledException"] = new { passed = false, detail = ex.ToString() };
        }

        var reportJson = JsonSerializer.Serialize(new
        {
            all_passed = allPassed,
            checks = results
        }, new JsonSerializerOptions { WriteIndented = true });

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await LosslessArchiver.WriteReplaceableCopyAtomicallyAsync(reportPath, Encoding.UTF8.GetBytes(reportJson));
    }
}

using System.Text;
using System.Text.Json;
using GhostCode.App.ViewModels;
using GhostCode.App.Views;
using GhostCode.Core.Archiver;
using GhostCode.Core.Browser;
using GhostCode.Core.Cloud;
using GhostCode.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GhostCode.App.Diagnostics;

/// <summary>
/// Executes live UI-thread verification across all WinUI 3 pages, integrated custom title bar geometry,
/// Ctrl+K search, Dashboard browser/problem ordering, 3-pane Archive + live Code Runner execution,
/// repaired Settings layout, Cloud controls, System Tray hide/restore, and optional PNG screenshot capture.
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
        var screenshotDir = Environment.GetEnvironmentVariable("GHOSTCODE_SCREENSHOT_DIR");
        var capturedScreenshots = new List<string>();

        void Record(string name, bool passed, string detail = "")
        {
            results[name] = new { passed, detail };
            if (!passed)
                allPassed = false;
        }

        async Task CaptureAsync(string fileName)
        {
            if (string.IsNullOrWhiteSpace(screenshotDir) || window.Content is null)
                return;
            await Task.Delay(350);
            var outPath = Path.Combine(screenshotDir, fileName);
            var theme = (window.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Light;
            var ok = await WindowScreenshotCapture.CaptureElementToPngAsync(window.Content, theme, outPath);
            if (ok)
                capturedScreenshots.Add(outPath);
        }

        try
        {
            await Task.Delay(350);

            Record("MicaBackdropAttached", window.SystemBackdrop is MicaBackdrop, window.SystemBackdrop?.GetType().Name ?? "null");

            // 1. Single-row integrated custom title bar geometry check
            var geo = window.GetTitleBarGeometrySnapshot();
            bool titleRowOk = geo.ExtendsContentIntoTitleBar
                && geo.TitleBarBottom > 0
                && geo.TitleBarBottom <= 58
                && geo.SearchBoxTop >= geo.TitleBarTop
                && geo.SearchBoxBottom <= geo.TitleBarBottom + 1
                && geo.SearchBoxRight > geo.SearchBoxLeft
                && Math.Abs(geo.SearchBoxTop - geo.LogoTop) < 20;
            Record("SingleRowTitleBarGeometry", titleRowOk,
                $"Extends={geo.ExtendsContentIntoTitleBar}, TitleBar=[{geo.TitleBarTop:F1}..{geo.TitleBarBottom:F1}], Search=[{geo.SearchBoxLeft:F1}..{geo.SearchBoxRight:F1}, Y={geo.SearchBoxTop:F1}..{geo.SearchBoxBottom:F1}], LogoY=[{geo.LogoTop:F1}..{geo.LogoBottom:F1}]");

            // 2. Scenario A: Fresh User Onboarding Card & Archive Empty State + 5-Page Navigation
            bool onboardingVisible = vm.OnboardingCardVisibility == Visibility.Visible;
            await CaptureAsync("00_onboarding_empty.png");

            var navOk = window.NavigateToTag("Archive") && window.CurrentPageType == typeof(ArchivePage);
            await Task.Delay(120);
            bool archiveEmptyOk = vm.ArchiveEmptyStateVisibility == Visibility.Visible && vm.ArchiveWorkspaceVisibility == Visibility.Collapsed;
            Record("FreshUserOnboardingAndArchiveEmptyState", onboardingVisible && archiveEmptyOk,
                $"onboarding={onboardingVisible}, archiveEmpty={archiveEmptyOk}");

            navOk &= window.NavigateToTag("Cloud") && window.CurrentPageType == typeof(CloudPage);
            await Task.Delay(120);
            navOk &= window.NavigateToTag("DevTools") && window.CurrentPageType == typeof(DevelopmentToolsPage);
            await Task.Delay(120);
            await CaptureAsync("08_devtools.png");
            navOk &= window.NavigateToTag("Settings") && window.CurrentPageType == typeof(SettingsPage);
            await Task.Delay(120);
            navOk &= window.NavigateToTag("Dashboard") && window.CurrentPageType == typeof(DashboardPage);
            Record("NavigationViewPages", navOk, window.CurrentPageType?.Name ?? "null");

            // 3. Dashboard: Empty Connected Browsers -> Authenticated Edge Heartbeat + Active Problem Card Ordering
            bool initialEmptyOk = vm.ConnectedBrowsers.Count == 0 && vm.NoConnectedBrowsersVisibility == Visibility.Visible;
            var bridge = App.Services.GetRequiredService<AuthenticatedBrowserBridge>();
            using var http = new HttpClient();
            var baseUrl = $"http://127.0.0.1:{bridge.ListeningPort}";
            var nonce = Guid.NewGuid().ToString("N");
            var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var hsReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/handshake");
            hsReq.Headers.Add("Origin", "chrome-extension://bifmfiolmohcdfknnicfikhhpipkokbc");
            hsReq.Headers.Add("X-GhostCode-Browser", "Edge");
            hsReq.Headers.Add("X-GhostCode-Ext-Version", "1.4.0");
            hsReq.Headers.Add("X-GhostCode-Pairing-Id", AuthenticatedBrowserBridge.DefaultPairingId);
            hsReq.Headers.Add("X-GhostCode-Nonce", nonce);
            hsReq.Headers.Add("X-GhostCode-Timestamp", ts.ToString());
            hsReq.Headers.Add("X-GhostCode-Proof", AuthenticatedBrowserBridge.ComputeHandshakeProof(config.BridgeAuthToken, nonce, ts));
            var hsResp = await http.SendAsync(hsReq);
            string sessionToken = string.Empty;
            if (hsResp.IsSuccessStatusCode)
            {
                using var hsDoc = JsonDocument.Parse(await hsResp.Content.ReadAsStringAsync());
                sessionToken = hsDoc.RootElement.GetProperty("session_token").GetString() ?? string.Empty;
            }

            if (!string.IsNullOrEmpty(sessionToken))
            {
                var ctxReq = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/context");
                ctxReq.Headers.Add("X-GhostCode-Token", sessionToken);
                ctxReq.Headers.Add("X-GhostCode-Browser", "Edge");
                ctxReq.Content = new StringContent(
                    """{"problem_code":"CPP0101","problem_title":"Tính Tổng Hai Số","platform":"PTIT","url":"https://code.ptit.edu.vn/student/question/CPP0101","tab_active":true,"browser_name":"Edge"}""",
                    Encoding.UTF8,
                    "application/json");
                await http.SendAsync(ctxReq);
            }

            var waitDeadline = Environment.TickCount64 + 3000;
            while (Environment.TickCount64 < waitDeadline && (vm.ConnectedBrowsers.Count == 0 || vm.ActiveCode != "CPP0101"))
                await Task.Delay(100);

            bool cardOrderOk = false;
            if (window.CurrentPageContent is DashboardPage dashPage
                && dashPage.FindName("ConnectedBrowsersCard") is FrameworkElement browsersCard
                && dashPage.FindName("DetectedProblemCard") is FrameworkElement problemCard)
            {
                dashPage.UpdateLayout();
                var bPt = browsersCard.TransformToVisual(dashPage).TransformPoint(new Windows.Foundation.Point(0, 0));
                var pPt = problemCard.TransformToVisual(dashPage).TransformPoint(new Windows.Foundation.Point(0, 0));
                cardOrderOk = bPt.Y < pPt.Y;
            }

            bool dashOk = initialEmptyOk
                && vm.ConnectedBrowsers.Count >= 1
                && vm.ActiveCode == "CPP0101"
                && vm.ActiveFilenamePreview == "CPP0101_Tinh_Tong_Hai_So.cpp"
                && cardOrderOk;
            Record("DashboardBrowsersAboveProblem", dashOk,
                $"emptyInit={initialEmptyOk}, connected={vm.ConnectedBrowsers.Count}, preview={vm.ActiveFilenamePreview}, cardOrder={cardOrderOk}");

            bool compactDevToolsOk = window.CurrentPageContent is DashboardPage dp2
                && dp2.FindName("DevToolsSummaryCard") is FrameworkElement
                && !string.IsNullOrWhiteSpace(vm.CompactToolchainSummaryText);
            Record("CompactDashboardDevToolsSummary", compactDevToolsOk, vm.CompactToolchainSummaryText);
            await CaptureAsync("01_dashboard.png");

            // 4. Settings: add folder, toggle recursive, refuse ArchiveDir, verify non-squeezed layout
            window.NavigateToTag("Settings");
            await Task.Delay(200);
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

            bool settingsLayoutOk = false;
            if (window.CurrentPageContent is SettingsPage sp
                && sp.FindName("SettingsScrollViewer") is ScrollViewer sv
                && sp.FindName("MonitoredFoldersItems") is ItemsControl ic)
            {
                sp.UpdateLayout();
                settingsLayoutOk = sv.ViewportWidth > 400 && ic.ActualWidth >= 200 && ic.ActualWidth <= sv.ViewportWidth + 2;
            }
            Record("RepairedSettingsLayout", settingsLayoutOk, $"settingsLayoutOk={settingsLayoutOk}");
            await CaptureAsync("03_settings.png");

            // 5. Language & Theme switching
            vm.SelectedThemeIndex = 1; // Dark
            bool darkOk = window.CurrentRootTheme == ElementTheme.Dark;
            vm.SelectedThemeIndex = 2; // Light
            bool lightOk = window.CurrentRootTheme == ElementTheme.Light;
            vm.SelectedThemeIndex = 0; // System
            bool themeOk = darkOk && lightOk && window.CurrentRootTheme == ElementTheme.Default;
            Record("LightDarkThemeSwitch", themeOk);

            vm.SelectedLanguageIndex = 1; // English
            bool enOk = vm.Strings.NavDashboard == "Dashboard" && vm.Strings.NavCloud == "Drive" && vm.UxStrings.NavDevTools == "Development Tools";
            vm.SelectedLanguageIndex = 0; // Vietnamese
            bool viOk = vm.Strings.NavDashboard == "Tổng quan"
                && vm.Strings.NavCloud == "Drive"
                && vm.UxStrings.NavDevTools == "Công cụ lập trình"
                && vm.ExpStrings.SamplesTabLabel == "Test mẫu"
                && vm.Strings.RunCodeButton == "Chạy"
                && vm.Strings.CopyCodeButton == "Sao chép code";
            Record("BilingualSwitch", enOk && viOk);

            bool noEnglishLeaksInVi = !vm.TitleBarBrowserBadgeText.Contains("0 Browsers", StringComparison.Ordinal)
                && !vm.TitleBarDriveBadgeText.Contains("Drive: Local", StringComparison.Ordinal)
                && !vm.LastSyncTimeText.Contains("Never", StringComparison.Ordinal)
                && vm.IdeStatuses.All(i => i.StatusBadge is not ("Running" or "Installed" or "Not Installed"))
                && vm.Toolchains.All(t => t.StatusBadge is not ("Ready" or "Unavailable"));
            Record("VietnameseLocalizationNoEnglishLeaks", viOk && noEnglishLeaksInVi,
                $"browser='{vm.TitleBarBrowserBadgeText}', drive='{vm.TitleBarDriveBadgeText}', lastSync='{vm.LastSyncTimeText}'");

            // 6. Archive & Ready path updates
            var newReady = Path.Combine(testWorkspaceRoot, "SelfTestReady");
            Directory.CreateDirectory(newReady);
            vm.SetReadyDirectory(newReady);
            bool pathsOk = string.Equals(vm.ReadyDir, newReady, StringComparison.OrdinalIgnoreCase);
            Record("ArchiveAndReadyPathControls", pathsOk && folderAddOk && recursiveToggleOk && excludedGuardOk);

            // 7. Diagnostic button
            await vm.RunDiagnosticCommand.ExecuteAsync(null);
            Record("DiagnosticButton", vm.LastDiagnosticResultText.Contains("PASS", StringComparison.Ordinal), vm.LastDiagnosticResultText);

            // 8. Save a real C++ source -> Ctrl+K Global Search -> Archive 3-pane -> Live Split I/O Execution
            var srcFile = Path.Combine(tempWatchDir, "selftest_sum.cpp");
            const string srcCode = "#include <iostream>\nint main(){ long long a, b; if (std::cin >> a >> b) std::cout << (a + b) << \"\\n\"; return 0; }\n";
            await File.WriteAllBytesAsync(srcFile, Encoding.UTF8.GetBytes(srcCode));

            var deadline = Environment.TickCount64 + 6000;
            while (Environment.TickCount64 < deadline)
            {
                await vm.RefreshAllAsync();
                if (vm.ArchiveVersions.Any(v => v.FileName.Contains("CPP0101", StringComparison.OrdinalIgnoreCase) || v.FileName.Contains("selftest_sum", StringComparison.OrdinalIgnoreCase)))
                    break;
                await Task.Delay(120);
            }

            await window.TriggerGlobalSearchQueryAsync("selftest_sum");
            await Task.Delay(250);
            bool globalSearchNavOk = window.CurrentPageType == typeof(ArchivePage)
                && vm.SelectedVersion is not null
                && vm.SelectedVersionSource.Contains("std::cin >> a >> b", StringComparison.Ordinal);
            Record("GlobalSearchCtrlKAndArchive", globalSearchNavOk, $"page={window.CurrentPageType?.Name}, sel={vm.SelectedVersion?.FileName}");

            if (vm.SelectedVersion is not null)
            {
                bool previewOk = vm.SelectedVersionSource.Contains("std::cin >> a >> b", StringComparison.Ordinal);
                Record("SourcePreviewAndDiagnostics", previewOk && vm.LastDetectedFileText.Contains("selftest_sum.cpp", StringComparison.Ordinal), vm.LastDetectedFileText);

                await vm.CopySelectedCodeCommand.ExecuteAsync(null);
                Record("CopyCodeCommand", vm.InfoBarSeverity == InfoBarSeverity.Success);

                // Quick Actions overlay check & screenshot
                vm.OpenQuickActionsPanel();
                await Task.Delay(250);
                bool qaOk = vm.IsQuickActionsOpen && vm.QuickActionsVisibility == Visibility.Visible;
                await CaptureAsync("06_quick_actions.png");
                vm.IsQuickActionsOpen = false;
                Record("QuickActionsOverlay", qaOk);

                // Live execution in split I/O panel + single primary Run/Stop button check
                bool singleRunBtnOk = window.CurrentPageContent is ArchivePage archPage
                    && archPage.FindName("RunSourceButton") is Button
                    && archPage.FindName("PanelRunButton") is null
                    && archPage.FindName("PanelStopButton") is null;

                vm.RunnerTabIndex = 1;
                vm.StdinText = "123 456\n";
                await vm.RunOrStopCodeCommand.ExecuteAsync(null);
                bool runnerOk = vm.StdoutText.Trim() == "579" && vm.IoOutputText.Trim() == "579";
                bool cleanDiagOk = vm.StderrPanelVisibility == Visibility.Collapsed;
                Record("LiveCodeRunnerSplitIo", runnerOk, $"stdout='{vm.StdoutText.Trim()}', status='{vm.RunnerStatusText}'");
                Record("SingleRunButtonAndSeparatedDiagnostics", singleRunBtnOk && runnerOk && cleanDiagOk,
                    $"singleBtn={singleRunBtnOk}, stdout='{vm.StdoutText.Trim()}', stderrCollapsed={cleanDiagOk}");
                await CaptureAsync("02_archive_runner.png");

                // Version Diff & Samples / Timeline tab
                vm.ToggleDiffModeCommand.Execute(null);
                bool diffOk = vm.IsDiffModeActive && vm.DiffModeVisibility == Visibility.Visible;
                vm.ToggleDiffModeCommand.Execute(null);

                await vm.AddCustomSampleAsync("123 456\n", "579\n");
                vm.RunnerTabIndex = 2;
                await vm.RunSelectedSampleCommand.ExecuteAsync(null);
                bool sampleOk = vm.SampleRunSummaryText.Contains("Sample Passed", StringComparison.Ordinal);
                await CaptureAsync("07_samples_timeline.png");
                vm.RunnerTabIndex = 1;
                Record("VersionDiffAndSamples", diffOk && sampleOk, $"diff={diffOk}, sample='{vm.SampleRunSummaryText}'");

                var restoredPath = Path.Combine(testWorkspaceRoot, "restored_selftest.cpp");
                bool restoreOk = await vm.RestoreSelectedVersionAsync(restoredPath)
                    && File.Exists(restoredPath)
                    && (await File.ReadAllTextAsync(restoredPath)).Contains("std::cin >> a >> b", StringComparison.Ordinal);

                // Tamper archive file AFTER capturing screenshots and verify integrity check blocks restore
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
                Record("LiveCodeRunnerSplitIo", false, "Skipped");
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

            // 9. Cloud controls, 3-Option Provider Exclusivity + No Stale OAuth Waiting Text + Screenshot
            window.NavigateToTag("Cloud");
            await Task.Delay(120);

            vm.SelectedCloudProviderIndex = 2; // LocalOnly ("Không sử dụng")
            bool localOnlyOk = vm.LocalOnlyCloudCardVisibility == Visibility.Visible
                && vm.GoogleDriveCardVisibility == Visibility.Collapsed
                && vm.OneDriveCardVisibility == Visibility.Collapsed
                && vm.ActiveCloudQueueVisibility == Visibility.Collapsed;

            vm.SelectedCloudProviderIndex = 1; // OneDrive
            bool oneDriveOk = vm.OneDriveCardVisibility == Visibility.Visible
                && vm.GoogleDriveCardVisibility == Visibility.Collapsed
                && vm.LocalOnlyCloudCardVisibility == Visibility.Collapsed
                && vm.ActiveCloudQueueVisibility == Visibility.Visible;

            vm.SelectedCloudProviderIndex = 0; // Google Drive
            bool gDriveOk = vm.GoogleDriveCardVisibility == Visibility.Visible
                && vm.OneDriveCardVisibility == Visibility.Collapsed
                && vm.LocalOnlyCloudCardVisibility == Visibility.Collapsed
                && vm.ActiveCloudQueueVisibility == Visibility.Visible
                && vm.OAuthStageVisibility == Visibility.Collapsed;
            Record("DriveThreeOptionExclusivityAndNoStaleOAuth", localOnlyOk && oneDriveOk && gDriveOk,
                $"local={localOnlyOk}, oneDrive={oneDriveOk}, gDrive={gDriveOk}");

            await vm.ConnectGoogleCommand.ExecuteAsync(null);
            bool connectGuardOk = vm.InfoBarSeverity == InfoBarSeverity.Warning;

            await vm.SaveOAuthClientAsync("self-test-client.apps.googleusercontent.com", "secret-never-in-config");
            var rawCfg = await File.ReadAllTextAsync(configPath);
            bool dpapiSecretOk = !rawCfg.Contains("secret-never-in-config", StringComparison.Ordinal)
                && File.Exists(credStore.ClientSecretFilePath)
                && await credStore.LoadClientSecretAsync() == "secret-never-in-config";

            await vm.SyncNowCommand.ExecuteAsync(null);
            await vm.PullCloudArchiveCommand.ExecuteAsync(null);
            await vm.RetryFailedCommand.ExecuteAsync(null);
            await vm.DisconnectGoogleCommand.ExecuteAsync(null);
            Record("CloudControlsAndDpapi", connectGuardOk && dpapiSecretOk && !vm.IsDriveConnected);
            await CaptureAsync("04_cloud.png");

            // 10. Dark Theme Dashboard + Live Mica Backdrop Switch
            window.NavigateToTag("Dashboard");
            vm.SelectedThemeIndex = 1; // Dark
            await CaptureAsync("05_dark_dashboard.png");
            vm.SelectedThemeIndex = 0; // Restore System

            vm.SelectedMicaKindIndex = 1; // MicaAlt
            bool altOk = window.SystemBackdrop is MicaBackdrop mbAlt && mbAlt.Kind == Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt;
            vm.SelectedMicaKindIndex = 2; // SolidFallback
            bool solidOk = window.SystemBackdrop is null;
            vm.SelectedMicaKindIndex = 0; // Restore Mica Base
            bool baseOk = window.SystemBackdrop is MicaBackdrop mbBase && mbBase.Kind == Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base;
            Record("MicaAppearanceSwitch", altOk && solidOk && baseOk);

            // 10b. Scenario H: Multi-resolution layout check across 1280x720, 1366x768, 1920x1080 in Light & Dark
            bool multiResOk = true;
            if (window.TrayManager is not null && window.Content is FrameworkElement rootEl)
            {
                var sizes = new[] { (1280, 720), (1366, 768), (1920, 1080) };
                foreach (var (w, h) in sizes)
                {
                    window.TrayManager.AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
                    foreach (var themeIdx in new[] { 1, 2 })
                    {
                        vm.SelectedThemeIndex = themeIdx;
                        await Task.Delay(80);
                        rootEl.UpdateLayout();
                        if (rootEl.ActualWidth <= 0 || rootEl.ActualHeight <= 0 || rootEl.ActualWidth > w + 16)
                            multiResOk = false;
                    }
                }
                vm.SelectedThemeIndex = 0;
                window.TrayManager.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 820));
            }
            Record("MultiResolutionAndThemeLayout", multiResOk);

            // 11. System Tray hide / restore
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
            screenshots = capturedScreenshots,
            checks = results
        }, new JsonSerializerOptions { WriteIndented = true });

        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await LosslessArchiver.WriteReplaceableCopyAtomicallyAsync(reportPath, Encoding.UTF8.GetBytes(reportJson));
    }
}

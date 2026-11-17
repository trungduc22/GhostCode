using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GhostCode.Core.Models;

namespace GhostCode.Tests;

public sealed class Phase7WinUiFunctionalTests : IDisposable
{
    private readonly string _root;

    public Phase7WinUiFunctionalTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "GhostCode_P7_WinUI_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task WinUI3App_ExercisesAllPagesTitleBarSearchRunnerSettingsTrayAndCapturesScreenshots()
    {
        var repoRoot = FindRepoRoot();
        var releaseExe = Path.Combine(repoRoot, "src", "GhostCode.App", "bin", "x64", "Release", "net10.0-windows10.0.19041.0", "win-x64", "GhostCode.App.exe");
        var debugExe = Path.Combine(repoRoot, "src", "GhostCode.App", "bin", "x64", "Debug", "net10.0-windows10.0.19041.0", "win-x64", "GhostCode.App.exe");

#if DEBUG
        var exePath = File.Exists(debugExe) ? debugExe : releaseExe;
#else
        var exePath = File.Exists(releaseExe) ? releaseExe : debugExe;
#endif
        Assert.True(File.Exists(exePath), $"WinUI 3 executable not found at {exePath}");

        int freePort;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            freePort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var cfgPath = Path.Combine(_root, "selftest_config.json");
        var reportPath = Path.Combine(_root, "selftest_report.json");
        var screenshotDir = Path.Combine(repoRoot, "artifacts", "screenshots");
        Directory.CreateDirectory(screenshotDir);

        var archiveDir = Path.Combine(_root, "Archive");
        var readyDir = Path.Combine(_root, "Ready");
        Directory.CreateDirectory(archiveDir);
        Directory.CreateDirectory(readyDir);

        var cfg = new GhostCodeConfig
        {
            WatchedFolders = new List<WatchedFolderConfig>(),
            ArchiveDir = archiveDir,
            ReadyDir = readyDir,
            DatabasePath = Path.Combine(archiveDir, "ghostcode.db"),
            HttpPort = freePort,
            BridgeAuthToken = "selftest-pairing-secret",
            DriveEnabled = false,
            MinimizeToTrayOnClose = true,
            Language = UiLanguage.Vi,
            Theme = ThemePreference.System
        };
        cfg.Save(cfgPath);

        var psi = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            UseShellExecute = false
        };
        psi.Environment["GHOSTCODE_CONFIG_PATH"] = cfgPath;
        psi.Environment["GHOSTCODE_SELF_TEST_REPORT"] = reportPath;
        psi.Environment["GHOSTCODE_SCREENSHOT_DIR"] = screenshotDir;

        using var proc = Process.Start(psi)!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("GhostCode.App.exe self-test run timed out.");
        }

        Assert.Equal(0, proc.ExitCode);
        Assert.True(File.Exists(reportPath), "WinUI 3 UI self-test report JSON was not generated.");

        var rawReport = await File.ReadAllTextAsync(reportPath);
        using var doc = JsonDocument.Parse(rawReport);
        Assert.True(doc.RootElement.GetProperty("all_passed").GetBoolean(), $"WinUI 3 UI self-test had failures:\n{rawReport}");

        var checks = doc.RootElement.GetProperty("checks");
        string[] expectedChecks =
        {
            "MicaBackdropAttached",
            "SingleRowTitleBarGeometry",
            "NavigationViewPages",
            "DashboardBrowsersAboveProblem",
            "RepairedSettingsLayout",
            "LightDarkThemeSwitch",
            "BilingualSwitch",
            "ArchiveAndReadyPathControls",
            "DiagnosticButton",
            "GlobalSearchCtrlKAndArchive",
            "SourcePreviewAndDiagnostics",
            "CopyCodeCommand",
            "LiveCodeRunnerSplitIo",
            "RestoreAndIntegrityCheck",
            "RemoveWatchedFolder",
            "CloudControlsAndDpapi",
            "QuickActionsOverlay",
            "VersionDiffAndSamples",
            "MicaAppearanceSwitch",
            "SystemTrayAndHideRestore"
        };
        foreach (var key in expectedChecks)
        {
            Assert.True(checks.TryGetProperty(key, out var item), $"Missing check '{key}' in report");
            Assert.True(item.GetProperty("passed").GetBoolean(), $"Check '{key}' failed: {item}");
        }

        string[] expectedScreenshots =
        {
            "01_dashboard.png",
            "02_archive_runner.png",
            "03_settings.png",
            "04_cloud.png",
            "05_dark_dashboard.png",
            "06_quick_actions.png",
            "07_samples_timeline.png"
        };
        foreach (var file in expectedScreenshots)
        {
            var full = Path.Combine(screenshotDir, file);
            Assert.True(File.Exists(full) && new FileInfo(full).Length > 1000, $"Screenshot missing or empty: {full}");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GhostCode.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate GhostCode.sln");
    }
}

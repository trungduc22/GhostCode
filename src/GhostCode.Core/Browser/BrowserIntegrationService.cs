using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using GhostCode.Core.Models;
using GhostCode.Core.Monitoring;
using Microsoft.Win32;

namespace GhostCode.Core.Browser;

public interface IBrowserEnvironmentProbe
{
    bool FileExists(string path);
    IReadOnlyCollection<string> GetRunningProcessNames();
    string? GetFileVersion(string path);
    string? ReadRegistryString(string fullKeyPath, string valueName);
}

public sealed class WindowsBrowserEnvironmentProbe : IBrowserEnvironmentProbe
{
    public bool FileExists(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    public IReadOnlyCollection<string> GetRunningProcessNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try { set.Add(p.ProcessName); } catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return set;
    }

    public string? GetFileVersion(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrWhiteSpace(info.ProductVersion) ? info.FileVersion : info.ProductVersion;
        }
        catch { return null; }
    }

    [SupportedOSPlatform("windows")]
    public string? ReadRegistryString(string fullKeyPath, string valueName)
    {
        try { return Registry.GetValue(fullKeyPath, valueName, null) as string; }
        catch { return null; }
    }
}

public sealed record BrowserIntegrationResult(
    bool Success,
    string ExtensionDirectory,
    string NativeManifestPath,
    string Message);

/// <summary>
/// Discovers Chrome, Microsoft Edge, Mozilla Firefox, and Thorium on Windows
/// (per-user, system-wide, App Paths registry, custom portable paths) and stages
/// Chromium/Firefox extension bundles + Native Messaging manifests cleanly.
/// </summary>
public sealed class BrowserIntegrationService
{
    public const string ChromiumDeterministicExtensionOrigin = "chrome-extension://bifmfiolmohcdfknnicfikhhpipkokbc/";
    public const string FirefoxExtensionId = "ghostcode-bridge@trungduc22.github.io";

    private readonly IBrowserEnvironmentProbe _probe;

    public BrowserIntegrationService(IBrowserEnvironmentProbe? probe = null)
    {
        _probe = probe ?? new WindowsBrowserEnvironmentProbe();
    }

    public List<(BrowserKind Kind, string DisplayName, bool IsInstalled, bool IsRunning, string? ExePath, string? Version)> DiscoverInstalledBrowsers(GhostCodeConfig config)
    {
        var running = _probe.GetRunningProcessNames();
        var list = new List<(BrowserKind, string, bool, bool, string?, string?)>();
        foreach (var kind in new[] { BrowserKind.Chrome, BrowserKind.Edge, BrowserKind.Firefox, BrowserKind.Thorium })
        {
            var (name, exe, ver, isRun) = ProbeSingle(kind, config, running);
            list.Add((kind, name, exe is not null, isRun, exe, ver));
        }
        return list;
    }

    private (string DisplayName, string? ExePath, string? Version, bool IsRunning) ProbeSingle(
        BrowserKind kind,
        GhostCodeConfig config,
        IReadOnlyCollection<string> running)
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>();

        if (config.CustomBrowserPaths.TryGetValue(kind.ToString(), out var customPath) && !string.IsNullOrWhiteSpace(customPath))
            candidates.Add(customPath);

        string displayName;
        string procName;
        switch (kind)
        {
            case BrowserKind.Chrome:
                displayName = "Google Chrome";
                procName = "chrome";
                AddRegistryAppPath(candidates, "chrome.exe");
                candidates.Add(Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"));
                candidates.Add(Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe"));
                candidates.Add(Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"));
                break;

            case BrowserKind.Edge:
                displayName = "Microsoft Edge";
                procName = "msedge";
                AddRegistryAppPath(candidates, "msedge.exe");
                candidates.Add(Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe"));
                candidates.Add(Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"));
                candidates.Add(Path.Combine(local, "Microsoft", "Edge", "Application", "msedge.exe"));
                break;

            case BrowserKind.Firefox:
                displayName = "Mozilla Firefox";
                procName = "firefox";
                AddRegistryAppPath(candidates, "firefox.exe");
                candidates.Add(Path.Combine(pf, "Mozilla Firefox", "firefox.exe"));
                candidates.Add(Path.Combine(pf86, "Mozilla Firefox", "firefox.exe"));
                candidates.Add(Path.Combine(local, "Mozilla Firefox", "firefox.exe"));
                break;

            default: // Thorium
                displayName = "Thorium";
                procName = "thorium";
                AddRegistryAppPath(candidates, "thorium.exe");
                candidates.Add(Path.Combine(local, "Thorium", "Application", "thorium.exe"));
                candidates.Add(Path.Combine(pf, "Thorium", "Application", "thorium.exe"));
                candidates.Add(Path.Combine(pf86, "Thorium", "Application", "thorium.exe"));
                break;
        }

        var found = candidates.FirstOrDefault(p => _probe.FileExists(p));
        var ver = found is not null ? _probe.GetFileVersion(found) : null;
        return (displayName, found, ver, running.Contains(procName));
    }

    private void AddRegistryAppPath(List<string> candidates, string exeName)
    {
        var lm = _probe.ReadRegistryString($@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}", string.Empty);
        if (!string.IsNullOrWhiteSpace(lm)) candidates.Add(lm.Trim('"'));
        var cu = _probe.ReadRegistryString($@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}", string.Empty);
        if (!string.IsNullOrWhiteSpace(cu)) candidates.Add(cu.Trim('"'));
    }

    public static string? LocateBundledExtensionFolder(string folderName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, folderName);
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "manifest.json")))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    [SupportedOSPlatform("windows")]
    public BrowserIntegrationResult PrepareIntegration(
        BrowserKind kind,
        GhostCodeConfig config,
        int listeningPort,
        string? hostExecutablePath = null,
        string? appDataOverride = null)
    {
        var baseExtRoot = Path.Combine(KnownFoldersResolver.ResolveAppDataDir(appDataOverride), "Extensions");
        var isFirefox = kind == BrowserKind.Firefox;
        var srcDir = LocateBundledExtensionFolder(isFirefox ? "browser-extension-firefox" : "browser-extension");
        var destDir = Path.Combine(baseExtRoot, isFirefox ? "firefox" : "chromium");
        Directory.CreateDirectory(destDir);

        if (srcDir is not null)
        {
            foreach (var file in Directory.GetFiles(srcDir))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("pairing.generated.json", StringComparison.OrdinalIgnoreCase))
                    continue;
                File.Copy(file, Path.Combine(destDir, name), overwrite: true);
            }
        }

        var pairingJson = JsonSerializer.Serialize(new
        {
            pairing_id = AuthenticatedBrowserBridge.DefaultPairingId,
            pairing_secret = config.BridgeAuthToken,
            port = listeningPort
        });
        File.WriteAllText(Path.Combine(destDir, "pairing.generated.json"), pairingJson, Encoding.UTF8);

        var nativeManifestDir = Path.Combine(KnownFoldersResolver.ResolveAppDataDir(appDataOverride), "NativeMessaging");
        Directory.CreateDirectory(nativeManifestDir);
        var hostExe = hostExecutablePath ?? Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "GhostCode.App.exe");
        var nmPath = Path.Combine(nativeManifestDir, isFirefox ? "com.ghostcode.bridge.firefox.json" : "com.ghostcode.bridge.chromium.json");

        if (isFirefox)
        {
            var ffManifest = JsonSerializer.Serialize(new
            {
                name = NativeMessagingHost.HostName,
                description = "GhostCode Native Messaging Bridge (Firefox)",
                path = hostExe,
                type = "stdio",
                allowed_extensions = new[] { FirefoxExtensionId }
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(nmPath, ffManifest, Encoding.UTF8);
        }
        else
        {
            NativeMessagingHost.WriteNativeMessagingManifest(nmPath, hostExe, ChromiumDeterministicExtensionOrigin);
        }

        if (OperatingSystem.IsWindows() && appDataOverride is null)
        {
            try
            {
                var regSubKey = kind switch
                {
                    BrowserKind.Chrome => $@"Software\Google\Chrome\NativeMessagingHosts\{NativeMessagingHost.HostName}",
                    BrowserKind.Edge => $@"Software\Microsoft\Edge\NativeMessagingHosts\{NativeMessagingHost.HostName}",
                    BrowserKind.Thorium => $@"Software\Thorium\NativeMessagingHosts\{NativeMessagingHost.HostName}",
                    BrowserKind.Firefox => $@"Software\Mozilla\NativeMessagingHosts\{NativeMessagingHost.HostName}",
                    _ => null
                };
                if (regSubKey is not null)
                {
                    using var key = Registry.CurrentUser.CreateSubKey(regSubKey);
                    key?.SetValue(null, nmPath);
                }
            }
            catch { }
        }

        var instructions = isFirefox
            ? $"Staged Firefox WebExtension at '{destDir}' and registered Native Messaging host. Open about:debugging -> Load Temporary Add-on (or install signed XPI)."
            : $"Staged Chromium MV3 extension at '{destDir}' (pairing port {listeningPort}) and registered Native Messaging host.";

        return new BrowserIntegrationResult(true, destDir, nmPath, instructions);
    }
}

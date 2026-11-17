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

    public IReadOnlyCollection<string> GetRunningProcessNames() =>
        WindowsProcessSnapshot.GetRunningProcessNames();

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
    string Message,
    string ExtensionsPageUrl = "",
    string? BrowserExecutablePath = null);

/// <summary>
/// Discovers Chrome, Microsoft Edge, Mozilla Firefox, and Thorium on Windows
/// (per-user, system-wide, App Paths registry, custom portable paths) and stages
/// per-browser extension bundles + headless Native Messaging manifests cleanly.
/// </summary>
public sealed class BrowserIntegrationService
{
    public const string ChromiumDeterministicExtensionId = "bifmfiolmohcdfknnicfikhhpipkokbc";
    public const string ChromiumDeterministicExtensionOrigin = "chrome-extension://bifmfiolmohcdfknnicfikhhpipkokbc/";
    public const string FirefoxExtensionId = "ghostcode-bridge@trungduc22.github.io";

    private readonly IBrowserEnvironmentProbe _probe;
    private readonly object _stateLock = new();
    private readonly HashSet<BrowserKind> _guidedInstalling = new();
    private readonly Dictionary<BrowserKind, (string DisplayName, string? ExePath, string? Version, string ProcName)> _staticInstallCache = new();
    private string _cachedCustomPathsKey = string.Empty;
    private long _staticInstallCacheExpiresAtMs;
    private readonly Dictionary<BrowserKind, (string Path, long LastWriteTicks, long Length, bool Detected)> _profileExtCache = new();

    public BrowserIntegrationService(IBrowserEnvironmentProbe? probe = null)
    {
        _probe = probe ?? new WindowsBrowserEnvironmentProbe();
    }

    public bool IsGuidedInstalling(BrowserKind kind)
    {
        lock (_stateLock)
            return _guidedInstalling.Contains(kind);
    }

    public void MarkGuidedInstalling(BrowserKind kind)
    {
        lock (_stateLock)
            _guidedInstalling.Add(kind);
    }

    public void ClearGuidedInstalling(BrowserKind kind)
    {
        lock (_stateLock)
            _guidedInstalling.Remove(kind);
    }

    public static string GetExtensionsPageUrl(BrowserKind kind) => kind switch
    {
        BrowserKind.Edge => "edge://extensions",
        BrowserKind.Firefox => "about:debugging#/runtime/this-firefox",
        _ => "chrome://extensions"
    };

    public static string GetRegistrySubKey(BrowserKind kind) => kind switch
    {
        BrowserKind.Chrome => $@"Software\Google\Chrome\NativeMessagingHosts\{NativeMessagingHost.HostName}",
        BrowserKind.Edge => $@"Software\Microsoft\Edge\NativeMessagingHosts\{NativeMessagingHost.HostName}",
        BrowserKind.Thorium => $@"Software\Thorium\NativeMessagingHosts\{NativeMessagingHost.HostName}",
        BrowserKind.Firefox => $@"Software\Mozilla\NativeMessagingHosts\{NativeMessagingHost.HostName}",
        _ => $@"Software\Google\Chrome\NativeMessagingHosts\{NativeMessagingHost.HostName}"
    };

    public static string GetStagedDirectory(BrowserKind kind, string? appDataOverride = null)
    {
        var baseExtRoot = Path.Combine(KnownFoldersResolver.ResolveAppDataDir(appDataOverride), "Extensions");
        return Path.Combine(baseExtRoot, kind.ToString().ToLowerInvariant());
    }

    public List<(BrowserKind Kind, string DisplayName, bool IsInstalled, bool IsRunning, string? ExePath, string? Version)> DiscoverInstalledBrowsers(
        GhostCodeConfig config,
        bool forceRescan = false)
    {
        var running = _probe.GetRunningProcessNames();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var customKey = string.Join("|", config.CustomBrowserPaths.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));

        bool refreshStatic;
        lock (_stateLock)
        {
            refreshStatic = forceRescan
                || _staticInstallCache.Count == 0
                || nowMs >= _staticInstallCacheExpiresAtMs
                || !string.Equals(_cachedCustomPathsKey, customKey, StringComparison.Ordinal);
        }

        if (refreshStatic)
        {
            var fresh = new Dictionary<BrowserKind, (string DisplayName, string? ExePath, string? Version, string ProcName)>();
            foreach (var kind in new[] { BrowserKind.Chrome, BrowserKind.Edge, BrowserKind.Firefox, BrowserKind.Thorium })
                fresh[kind] = ProbeSingleStatic(kind, config);

            lock (_stateLock)
            {
                _staticInstallCache.Clear();
                foreach (var kv in fresh)
                    _staticInstallCache[kv.Key] = kv.Value;
                _cachedCustomPathsKey = customKey;
                _staticInstallCacheExpiresAtMs = nowMs + 60_000;
            }
        }

        var list = new List<(BrowserKind, string, bool, bool, string?, string?)>(4);
        lock (_stateLock)
        {
            foreach (var kind in new[] { BrowserKind.Chrome, BrowserKind.Edge, BrowserKind.Firefox, BrowserKind.Thorium })
            {
                var entry = _staticInstallCache[kind];
                list.Add((kind, entry.DisplayName, entry.ExePath is not null, running.Contains(entry.ProcName), entry.ExePath, entry.Version));
            }
        }
        return list;
    }

    private (string DisplayName, string? ExePath, string? Version, string ProcName) ProbeSingleStatic(
        BrowserKind kind,
        GhostCodeConfig config)
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
        return (displayName, found, ver, procName);
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
        var isFirefox = kind == BrowserKind.Firefox;
        var srcDir = LocateBundledExtensionFolder(isFirefox ? "browser-extension-firefox" : "browser-extension");
        var destDir = GetStagedDirectory(kind, appDataOverride);
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
            port = listeningPort,
            browser_name = kind.ToString()
        });
        File.WriteAllText(Path.Combine(destDir, "pairing.generated.json"), pairingJson, Encoding.UTF8);

        var nativeManifestDir = Path.Combine(KnownFoldersResolver.ResolveAppDataDir(appDataOverride), "NativeMessaging");
        Directory.CreateDirectory(nativeManifestDir);
        var hostExe = hostExecutablePath ?? Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "GhostCode.App.exe");
        var kindTag = kind.ToString().ToLowerInvariant();
        var nmPath = Path.Combine(nativeManifestDir, $"com.ghostcode.bridge.{kindTag}.json");
        var cmdWrapperPath = Path.Combine(nativeManifestDir, $"com.ghostcode.bridge.{kindTag}.cmd");
        var cmdContent = $"@echo off\r\n\"{hostExe}\" --native-messaging-host --browser {kind} %*\r\n";
        File.WriteAllText(cmdWrapperPath, cmdContent, Encoding.ASCII);

        if (isFirefox)
        {
            var ffManifest = JsonSerializer.Serialize(new
            {
                name = NativeMessagingHost.HostName,
                description = "GhostCode Native Messaging Bridge (Firefox)",
                path = cmdWrapperPath,
                type = "stdio",
                allowed_extensions = new[] { FirefoxExtensionId }
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(nmPath, ffManifest, Encoding.UTF8);
        }
        else
        {
            NativeMessagingHost.WriteNativeMessagingManifest(nmPath, cmdWrapperPath, ChromiumDeterministicExtensionOrigin);
        }

        if (OperatingSystem.IsWindows() && appDataOverride is null)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(GetRegistrySubKey(kind));
                key?.SetValue(null, nmPath);
            }
            catch { }
        }

        var pageUrl = GetExtensionsPageUrl(kind);
        var discoveredExe = DiscoverInstalledBrowsers(config).FirstOrDefault(b => b.Kind == kind).ExePath;
        var instructions = isFirefox
            ? $"Staged Firefox WebExtension at '{destDir}' and registered Native Messaging host. Open {pageUrl} -> Load Temporary Add-on."
            : $"Staged {kind} MV3 extension at '{destDir}' (port {listeningPort}) and registered Native Messaging host.";

        return new BrowserIntegrationResult(true, destDir, nmPath, instructions, pageUrl, discoveredExe);
    }

    [SupportedOSPlatform("windows")]
    public BrowserIntegrationResult LaunchAssistedInstall(
        BrowserKind kind,
        GhostCodeConfig config,
        int listeningPort,
        bool launchExternalProcesses = true,
        string? hostExecutablePath = null,
        string? appDataOverride = null)
    {
        var result = PrepareIntegration(kind, config, listeningPort, hostExecutablePath, appDataOverride);
        MarkGuidedInstalling(kind);

        if (launchExternalProcesses)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(result.BrowserExecutablePath) && File.Exists(result.BrowserExecutablePath))
                {
                    Process.Start(new ProcessStartInfo(result.BrowserExecutablePath, result.ExtensionsPageUrl)
                    {
                        UseShellExecute = true
                    });
                }
            }
            catch { }

            try
            {
                if (Directory.Exists(result.ExtensionDirectory))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", result.ExtensionDirectory)
                    {
                        UseShellExecute = true
                    });
                }
            }
            catch { }
        }

        return result;
    }

    public bool IsNativeHostHealthy(BrowserKind kind, string? appDataOverride = null)
    {
        if (!OperatingSystem.IsWindows() || _probe is not WindowsBrowserEnvironmentProbe)
            return false;

        var regPath = appDataOverride is null
            ? _probe.ReadRegistryString($@"HKEY_CURRENT_USER\{GetRegistrySubKey(kind)}", string.Empty)
            : Path.Combine(KnownFoldersResolver.ResolveAppDataDir(appDataOverride), "NativeMessaging", $"com.ghostcode.bridge.{kind.ToString().ToLowerInvariant()}.json");

        if (string.IsNullOrWhiteSpace(regPath) || !File.Exists(regPath))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(regPath));
            if (!doc.RootElement.TryGetProperty("path", out var pathEl))
                return false;
            var target = pathEl.GetString();
            return !string.IsNullOrWhiteSpace(target) && File.Exists(target);
        }
        catch
        {
            return false;
        }
    }

    public bool IsExtensionDetectedInBrowserProfile(BrowserKind kind)
    {
        if (!OperatingSystem.IsWindows() || _probe is not WindowsBrowserEnvironmentProbe)
            return false;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            return false;

        IEnumerable<string> prefCandidates = kind switch
        {
            BrowserKind.Edge => new[]
            {
                Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Secure Preferences"),
                Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Preferences")
            },
            BrowserKind.Thorium => new[]
            {
                Path.Combine(local, "Thorium", "User Data", "Default", "Secure Preferences"),
                Path.Combine(local, "Thorium", "User Data", "Default", "Preferences")
            },
            BrowserKind.Chrome => new[]
            {
                Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Secure Preferences"),
                Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Preferences")
            },
            _ => Array.Empty<string>()
        };

        foreach (var prefPath in prefCandidates)
        {
            try
            {
                if (!File.Exists(prefPath))
                    continue;
                var fi = new FileInfo(prefPath);
                lock (_stateLock)
                {
                    if (_profileExtCache.TryGetValue(kind, out var cached)
                        && string.Equals(cached.Path, prefPath, StringComparison.OrdinalIgnoreCase)
                        && cached.LastWriteTicks == fi.LastWriteTimeUtc.Ticks
                        && cached.Length == fi.Length)
                    {
                        if (cached.Detected)
                            return true;
                        continue;
                    }
                }
                using var fs = new FileStream(prefPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                var text = sr.ReadToEnd();
                bool detected = text.Contains(ChromiumDeterministicExtensionId, StringComparison.OrdinalIgnoreCase);
                lock (_stateLock)
                {
                    _profileExtCache[kind] = (prefPath, fi.LastWriteTimeUtc.Ticks, fi.Length, detected);
                }
                if (detected)
                    return true;
            }
            catch { }
        }
        return false;
    }
}

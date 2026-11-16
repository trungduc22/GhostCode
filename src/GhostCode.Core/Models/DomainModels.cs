using System.Text.Json;
using System.Text.Json.Serialization;
using GhostCode.Core.Analysis;
using GhostCode.Core.Monitoring;

namespace GhostCode.Core.Models;

public enum OjPlatform
{
    Ptit,
    Codeforces,
    Vnoj,
    LeetCode,
    HackerRank,
    Offline,
    Other
}

public enum SourceLanguage
{
    C,
    Cpp,
    Header,
    Python
}

public enum UiLanguage
{
    Vi,
    En
}

public enum ThemePreference
{
    System,
    Dark,
    Light
}

public enum UploadStatus
{
    Pending,
    Uploading,
    Synced,
    Failed
}

public enum BrowserKind
{
    Chrome,
    Edge,
    Firefox,
    Thorium
}

public enum BrowserConnectionState
{
    NotInstalled,
    Installed,
    Running,
    ExtensionConnected,
    ExtensionDisconnected,
    ExtensionError
}

public enum VersionAvailability
{
    Synced,
    LocalOnly,
    CloudOnly,
    Conflict,
    Error
}

public enum ConflictResolutionPolicy
{
    PreserveBothBranches,
    PreferLocal,
    PreferRemote
}

public sealed record BrowserStatusInfo(
    BrowserKind Kind,
    string DisplayName,
    bool IsInstalled,
    bool IsRunning,
    string? ExecutablePath,
    string? Version,
    BrowserConnectionState ConnectionState,
    string? ExtensionVersion,
    long? LastHeartbeatUnixMs,
    string StatusDetail);

public sealed record IdeStatusInfo(
    string Name,
    bool IsInstalled,
    bool IsRunning,
    string? ExecutablePath,
    string? Version,
    string SupportedLanguages,
    string ToolchainSummary);

public sealed record ToolchainInfo(
    string Kind,
    string DisplayName,
    bool IsAvailable,
    string? ExecutablePath,
    string? Version,
    string DefaultFlags,
    string StatusDetail);

public sealed record ArchiveHistoryEvent(
    string Id,
    string EventType,
    string SourcePath,
    string? VersionId,
    string Summary,
    long OccurredAtUnixMs,
    string DeviceName);

public sealed record ProblemContext(
    OjPlatform Platform,
    string Code,
    string Title,
    string Url,
    string Source,
    long SeenAtUnixMs,
    bool TabActive = true,
    string BrowserName = "")
{
    public string FilenamePreview
    {
        get
        {
            var lang = SourceLanguage.Cpp;
            var stem = SourceAnalyzer.BuildProblemBaseName(this, "Untitled1.cpp", string.Empty, lang);
            var ext = SourceAnalyzer.TargetExtension(this, lang, string.Empty);
            return $"{stem}.{ext}";
        }
    }
}

public sealed record WatchedFolderConfig(
    string Path,
    bool Enabled = true,
    bool Recursive = true,
    string Label = "");

public sealed record GoogleOAuthClientConfig(
    string ClientId = "",
    [property: JsonIgnore] string ClientSecret = "");

public sealed record FileSnapshotRecord(
    string SourcePath,
    string Sha256,
    string NormHash,
    byte[] RawBytes,
    string EncodingName,
    string? ProblemCode,
    string? ProblemTitle,
    string Platform,
    long UpdatedAtUnixMs);

public sealed record ArchiveVersionRecord(
    string Id,
    int VersionNum,
    string SourcePath,
    string ArchivePath,
    string? ReadyPath,
    string FileName,
    string Sha256,
    string NormHash,
    long SizeBytes,
    string? ProblemCode,
    string? ProblemTitle,
    string Platform,
    long CreatedAtUnixMs,
    bool IsLocalAvailable = true,
    string? DriveFileId = null,
    bool IsConflictBranch = false)
{
    [JsonIgnore]
    public VersionAvailability Availability => !IsLocalAvailable
        ? VersionAvailability.CloudOnly
        : IsConflictBranch
            ? VersionAvailability.Conflict
            : !string.IsNullOrEmpty(DriveFileId)
                ? VersionAvailability.Synced
                : VersionAvailability.LocalOnly;
}

public sealed record UploadQueueItem(
    string VersionId,
    UploadStatus Status,
    int AttemptCount,
    long NextRetryAtUnixMs,
    string? DriveFileId,
    string? LastError,
    long UpdatedAtUnixMs);

public sealed record UploadQueueView(
    UploadQueueItem Queue,
    ArchiveVersionRecord Version);

public sealed record MonitorEventRecord(
    long Id,
    string FolderPath,
    string FilePath,
    string EventType,
    string Message,
    long OccurredAtUnixMs);

public sealed record ArchiveStats(
    int TotalVersions,
    int PendingUploads,
    int FailedUploads,
    int SyncedUploads,
    long? LastSuccessfulSyncUnixMs);

public sealed class GhostCodeConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public List<WatchedFolderConfig> WatchedFolders { get; set; } = new();
    public string ArchiveDir { get; set; } = string.Empty;
    public string ReadyDir { get; set; } = string.Empty;
    public string DatabasePath { get; set; } = string.Empty;
    public UiLanguage Language { get; set; } = UiLanguage.Vi;
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool LaunchAtStartup { get; set; } = false;
    public bool AutoCopyReadyPath { get; set; } = true;
    public bool DriveEnabled { get; set; } = true;
    public string DriveFolderName { get; set; } = "CodeArchive";
    public string? DriveRootFolderId { get; set; }
    public int HttpPort { get; set; } = 48123;
    public int ContextExpirationSeconds { get; set; } = 1200;
    public int SessionTokenTtlSeconds { get; set; } = 300;
    public string BridgeAuthToken { get; set; } = string.Empty;
    [JsonIgnore]
    public string? PairingFilePath { get; set; }
    public GoogleOAuthClientConfig OAuthClient { get; set; } = new();
    public string CCompilerPath { get; set; } = string.Empty;
    public string CppCompilerPath { get; set; } = string.Empty;
    public string PythonInterpreterPath { get; set; } = string.Empty;
    public string CStandard { get; set; } = "c11";
    public string CppStandard { get; set; } = "auto";
    public int ExecutionTimeoutMs { get; set; } = 5000;
    public int ExecutionMemoryLimitMb { get; set; } = 256;
    public ConflictResolutionPolicy ConflictPolicy { get; set; } = ConflictResolutionPolicy.PreserveBothBranches;
    public Dictionary<string, string> CustomBrowserPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> CustomIdePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string DefaultConfigPath(string? localAppDataOverride = null) =>
        Path.Combine(KnownFoldersResolver.ResolveAppDataDir(localAppDataOverride), "config.json");

    public static GhostCodeConfig CreateDefault(string? userProfileOverride = null, string? localAppDataOverride = null)
    {
        var baseDocs = KnownFoldersResolver.ResolveDefaultStorageBase(userProfileOverride);
        var appData = KnownFoldersResolver.ResolveAppDataDir(localAppDataOverride);
        var archiveDir = Path.Combine(baseDocs, "GhostCodeArchive");
        return new GhostCodeConfig
        {
            WatchedFolders = KnownFoldersResolver.DiscoverDefaultWatchedFolders(userProfileOverride),
            ArchiveDir = archiveDir,
            ReadyDir = Path.Combine(baseDocs, "CodePTIT_Ready"),
            DatabasePath = Path.Combine(appData, "ghostcode.db"),
            Language = UiLanguage.Vi,
            Theme = ThemePreference.System,
            MinimizeToTrayOnClose = true,
            AutoCopyReadyPath = true,
            DriveEnabled = true,
            DriveFolderName = "CodeArchive",
            HttpPort = 48123,
            ContextExpirationSeconds = 1200,
            BridgeAuthToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))
        };
    }

    public static GhostCodeConfig LoadOrDefault(
        string configPath,
        string? userProfileOverride = null,
        string? localAppDataOverride = null)
    {
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                var parsed = JsonSerializer.Deserialize<GhostCodeConfig>(json, JsonOptions);
                if (parsed is not null)
                {
                    parsed.EnsureDefaults(userProfileOverride, localAppDataOverride);
                    return parsed;
                }
            }
            catch
            {
                // Back up corrupted config instead of crashing or losing evidence
                try
                {
                    var bak = $"{configPath}.corrupt.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.bak";
                    File.Copy(configPath, bak, overwrite: true);
                }
                catch { }
            }
        }

        var fresh = CreateDefault(userProfileOverride, localAppDataOverride);
        try { fresh.Save(configPath); } catch { }
        return fresh;
    }

    public void EnsureDefaults(string? userProfileOverride = null, string? localAppDataOverride = null)
    {
        var def = CreateDefault(userProfileOverride, localAppDataOverride);
        if (WatchedFolders is null)
            WatchedFolders = def.WatchedFolders;
        WatchedFolders = WatchedFolders
            .Where(w => !string.IsNullOrWhiteSpace(w.Path) && !KnownFoldersResolver.IsDriveRoot(w.Path))
            .GroupBy(w => Path.GetFullPath(w.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (string.IsNullOrWhiteSpace(ArchiveDir))
            ArchiveDir = def.ArchiveDir;
        if (string.IsNullOrWhiteSpace(ReadyDir))
            ReadyDir = def.ReadyDir;
        if (string.IsNullOrWhiteSpace(DatabasePath))
            DatabasePath = def.DatabasePath;
        if (HttpPort <= 0 || HttpPort > 65535)
            HttpPort = 48123;
        if (ContextExpirationSeconds <= 0)
            ContextExpirationSeconds = 1200;
        if (string.IsNullOrWhiteSpace(BridgeAuthToken))
            BridgeAuthToken = def.BridgeAuthToken;
        if (string.IsNullOrWhiteSpace(DriveFolderName))
            DriveFolderName = "CodeArchive";
        OAuthClient ??= new GoogleOAuthClientConfig();
        CStandard = string.IsNullOrWhiteSpace(CStandard) ? "c11" : CStandard;
        CppStandard = string.IsNullOrWhiteSpace(CppStandard) ? "auto" : CppStandard;
        if (ExecutionTimeoutMs <= 0) ExecutionTimeoutMs = 5000;
        if (ExecutionMemoryLimitMb <= 0) ExecutionMemoryLimitMb = 256;
        CustomBrowserPaths ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CustomIdePaths ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public void Save(string configPath)
    {
        var dir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        var tmp = $"{configPath}.tmp.{Guid.NewGuid():N}";
        File.WriteAllText(tmp, json);
        File.Move(tmp, configPath, overwrite: true);
    }

    public string ExportToJson()
    {
        var sanitized = new GhostCodeConfig
        {
            WatchedFolders = WatchedFolders.ToList(),
            ArchiveDir = ArchiveDir,
            ReadyDir = ReadyDir,
            DatabasePath = DatabasePath,
            Language = Language,
            Theme = Theme,
            MinimizeToTrayOnClose = MinimizeToTrayOnClose,
            LaunchAtStartup = LaunchAtStartup,
            AutoCopyReadyPath = AutoCopyReadyPath,
            DriveEnabled = DriveEnabled,
            DriveFolderName = DriveFolderName,
            DriveRootFolderId = DriveRootFolderId,
            HttpPort = HttpPort,
            ContextExpirationSeconds = ContextExpirationSeconds,
            SessionTokenTtlSeconds = SessionTokenTtlSeconds,
            BridgeAuthToken = string.IsNullOrEmpty(BridgeAuthToken) ? string.Empty : "[REDACTED]",
            OAuthClient = new GoogleOAuthClientConfig(OAuthClient.ClientId, string.Empty),
            CCompilerPath = CCompilerPath,
            CppCompilerPath = CppCompilerPath,
            PythonInterpreterPath = PythonInterpreterPath,
            CStandard = CStandard,
            CppStandard = CppStandard,
            ExecutionTimeoutMs = ExecutionTimeoutMs,
            ExecutionMemoryLimitMb = ExecutionMemoryLimitMb,
            ConflictPolicy = ConflictPolicy,
            CustomBrowserPaths = new Dictionary<string, string>(CustomBrowserPaths, StringComparer.OrdinalIgnoreCase),
            CustomIdePaths = new Dictionary<string, string>(CustomIdePaths, StringComparer.OrdinalIgnoreCase)
        };
        return JsonSerializer.Serialize(sanitized, JsonOptions);
    }
}

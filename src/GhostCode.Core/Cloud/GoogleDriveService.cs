using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GhostCode.Core.Analysis;
using GhostCode.Core.Archiver;
using GhostCode.Core.Models;
using GhostCode.Core.Notifications;
using GhostCode.Core.Storage;

namespace GhostCode.Core.Cloud;

public sealed record DriveSyncProgress(
    int Completed,
    int Total,
    string CurrentFileName,
    double PercentComplete);

public sealed record DriveSyncReport(
    int UploadedCount,
    int FailedCount,
    int SkippedDuplicateCount,
    bool Connected,
    string? LastError);

public sealed record DrivePullReport(
    int DiscoveredRemoteVersions,
    int NewCloudOnlyCount,
    int ConflictBranchCount,
    int HistoryEventsImported,
    bool Connected,
    string? Error);

public sealed record DriveDownloadResult(
    bool Success,
    string? LocalArchivePath,
    string? Error);

public enum OAuthConnectionStage
{
    Idle,
    WaitingForBrowserCallback,
    CallbackReceivedExchangingToken,
    Connected,
    Failed
}

public sealed record DisconnectResult(
    bool LocalCredentialsCleared,
    bool RemoteRevocationSucceeded,
    string? RemoteError);

/// <summary>
/// Native Google Drive v3 client using OAuth 2.0 Desktop PKCE, DPAPI token persistence,
/// SHA-256 duplicate avoidance, bounded exponential backoff, and explicit server confirmation.
/// </summary>
public sealed class GoogleDriveService : ICloudArchiveProvider, IAsyncDisposable
{
    public const string DriveScope = "https://www.googleapis.com/auth/drive.file openid email profile";

    private readonly GhostCodeConfig _config;
    private readonly SqliteArchiveStore _store;
    private readonly DpapiCredentialStore _credentialStore;
    private readonly HttpClient _http;
    private readonly NotificationCoordinator? _notifications;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private CancellationTokenSource? _bgCts;
    private Task? _bgTask;

    public GoogleDriveService(
        GhostCodeConfig config,
        SqliteArchiveStore store,
        DpapiCredentialStore credentialStore,
        HttpClient? httpClient = null,
        NotificationCoordinator? notifications = null)
    {
        _config = config;
        _store = store;
        _credentialStore = credentialStore;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        _notifications = notifications;
    }

    public CloudProviderKind ProviderKind => CloudProviderKind.GoogleDrive;
    public string DisplayName => "Google Drive";
    public bool IsConfigured => HasOAuthClientConfigured;
    public bool HasOAuthClientConfigured => !string.IsNullOrWhiteSpace(_config.OAuthClient?.ClientId);

    public async Task<CloudAccountInfo> GetAccountInfoAsync(CancellationToken ct = default)
    {
        var tok = await _credentialStore.LoadAsync(ct);
        return tok is null
            ? new CloudAccountInfo(CloudProviderKind.GoogleDrive, false, string.Empty, "Not connected")
            : new CloudAccountInfo(CloudProviderKind.GoogleDrive, true, tok.Email, tok.DisplayName);
    }

    public Task<DrivePullReport> PullAndReconcileAsync(IProgress<DriveSyncProgress>? progress = null, CancellationToken ct = default) =>
        PullAndReconcileFromDriveAsync(progress, ct);

    public async Task<IReadOnlyList<CloudObjectMetadata>> ListRemoteObjectsAsync(CancellationToken ct = default)
    {
        var tokens = await EnsureValidTokenAsync(ct);
        if (tokens is null)
            return Array.Empty<CloudObjectMetadata>();
        var folderId = await EnsureRemoteFolderIdAsync(tokens.AccessToken, ct);
        var entries = await ListFolderFilesAsync(tokens.AccessToken, folderId, ct);
        return entries
            .Select(e =>
            {
                e.AppProperties.TryGetValue("ghostcode_sha256", out var propSha);
                return new CloudObjectMetadata(e.Id, e.Name, e.Size, propSha ?? e.Sha256Checksum, e.CreatedUnixMs);
            })
            .ToList();
    }

    public async Task<byte[]> DownloadRawBytesAsync(string remoteId, CancellationToken ct = default)
    {
        var tokens = await EnsureValidTokenAsync(ct)
            ?? throw new InvalidOperationException("Not connected to Google Drive.");
        return await DownloadRawFileBytesAsync(tokens.AccessToken, remoteId, ct);
    }

    public async Task<string> UploadVersionBytesAsync(ArchiveVersionRecord version, byte[] rawBytes, CancellationToken ct = default)
    {
        var tokens = await EnsureValidTokenAsync(ct)
            ?? throw new InvalidOperationException("Not connected to Google Drive.");
        var folderId = await EnsureRemoteFolderIdAsync(tokens.AccessToken, ct);
        var existingId = await FindExistingDriveFileByShaAsync(tokens.AccessToken, folderId, version.Sha256, ct);
        if (!string.IsNullOrEmpty(existingId))
            return existingId;
        return await UploadMultipartAsync(tokens.AccessToken, folderId, version, rawBytes, ct);
    }

    public event Action<DriveSyncReport>? SyncCompleted;
    public event Action<OAuthConnectionStage, string>? AuthStageChanged;

    public async Task<GoogleAuthTokens?> GetConnectedAccountAsync(CancellationToken ct = default) =>
        await _credentialStore.LoadAsync(ct);

    public void StartBackgroundSync()
    {
        if (_bgCts is not null)
            return;
        _bgCts = new CancellationTokenSource();
        _bgTask = Task.Run(() => BackgroundLoopAsync(_bgCts.Token));
    }

    private async Task BackgroundLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
                if (!_config.SyncPausedManually
                    && _config.PrimaryCloudProvider == CloudProviderKind.GoogleDrive
                    && _config.DriveEnabled
                    && await GetConnectedAccountAsync(ct) is not null)
                {
                    await SyncPendingAsync(progress: null, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch { }
        }
    }

    /// <summary>
    /// Runs an interactive OAuth 2.0 Authorization Code + PKCE flow via system browser
    /// and a temporary loopback redirect listener.
    /// </summary>
    public async Task<GoogleAuthTokens> ConnectWithBrowserAsync(
        Action<string>? openBrowser = null,
        CancellationToken ct = default)
    {
        if (!HasOAuthClientConfigured)
        {
            const string msg = "Google OAuth Desktop Client ID is not configured. Open Cloud OAuth setup and enter your Google Cloud Desktop Client ID.";
            AuthStageChanged?.Invoke(OAuthConnectionStage.Failed, msg);
            throw new InvalidOperationException(msg);
        }

        var verifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64UrlEncode(RandomNumberGenerator.GetBytes(16));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirectUri = $"http://127.0.0.1:{port}/callback";

        try
        {
            AuthStageChanged?.Invoke(OAuthConnectionStage.WaitingForBrowserCallback, "Waiting for Google sign-in authorization in browser...");

            var authUrl = "https://accounts.google.com/o/oauth2/v2/auth"
                + $"?client_id={Uri.EscapeDataString(_config.OAuthClient.ClientId)}"
                + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
                + "&response_type=code"
                + $"&scope={Uri.EscapeDataString(DriveScope)}"
                + "&access_type=offline"
                + "&prompt=consent"
                + $"&code_challenge={Uri.EscapeDataString(challenge)}"
                + "&code_challenge_method=S256"
                + $"&state={Uri.EscapeDataString(state)}";

            if (openBrowser is not null)
            {
                openBrowser(authUrl);
            }
            else
            {
                Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));
            using var reg = timeoutCts.Token.Register(() => { try { listener.Stop(); } catch { } });

            using var client = await listener.AcceptTcpClientAsync(timeoutCts.Token);
            await using var stream = client.GetStream();

            var buf = new byte[8192];
            int read = await stream.ReadAsync(buf, timeoutCts.Token);
            var reqText = Encoding.ASCII.GetString(buf, 0, read);
            var firstLine = reqText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            var parts = firstLine.Split(' ');
            var target = parts.Length >= 2 ? parts[1] : "/";
            var uri = new Uri("http://127.0.0.1" + target);
            var query = ParseQueryString(uri.Query);

            query.TryGetValue("state", out var gotState);
            query.TryGetValue("code", out var code);
            query.TryGetValue("error", out var error);

            bool callbackValid = string.Equals(gotState, state, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(code)
                && string.IsNullOrEmpty(error);

            if (!callbackValid)
            {
                var reason = error ?? "State mismatch or authorization cancelled.";
                await WriteOAuthBrowserResponseAsync(
                    stream,
                    400,
                    "Bad Request",
                    $"<html><body style='font-family:Segoe UI,sans-serif;padding:40px'><h2>Authorization Failed</h2><p>{WebUtility.HtmlEncode(reason)}</p></body></html>",
                    ct);
                AuthStageChanged?.Invoke(OAuthConnectionStage.Failed, reason);
                throw new InvalidOperationException($"Google authorization failed: {reason}");
            }

            AuthStageChanged?.Invoke(OAuthConnectionStage.CallbackReceivedExchangingToken, "Authorization callback received. Exchanging PKCE code for tokens...");

            try
            {
                var tokens = await ExchangeCodeForTokensAsync(code!, verifier, redirectUri, ct);
                await _credentialStore.SaveAsync(tokens, ct);

                await WriteOAuthBrowserResponseAsync(
                    stream,
                    200,
                    "OK",
                    $"<html><body style='font-family:Segoe UI,sans-serif;padding:40px'><h2>Token Exchange Completed &amp; Account Connected</h2><p>Connected account: <strong>{WebUtility.HtmlEncode(tokens.Email)}</strong>. You can close this tab and return to GhostCode.</p></body></html>",
                    ct);

                AuthStageChanged?.Invoke(OAuthConnectionStage.Connected, $"Connected as {tokens.Email}");
                return tokens;
            }
            catch (Exception ex)
            {
                try
                {
                    await WriteOAuthBrowserResponseAsync(
                        stream,
                        500,
                        "Internal Server Error",
                        $"<html><body style='font-family:Segoe UI,sans-serif;padding:40px'><h2>Token Exchange Failed</h2><p>Authorization callback was received, but token exchange failed: {WebUtility.HtmlEncode(ex.Message)}</p></body></html>",
                        CancellationToken.None);
                }
                catch { }
                AuthStageChanged?.Invoke(OAuthConnectionStage.Failed, ex.Message);
                throw;
            }
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }

    private static async Task WriteOAuthBrowserResponseAsync(
        NetworkStream stream,
        int statusCode,
        string statusText,
        string html,
        CancellationToken ct)
    {
        var htmlBytes = Encoding.UTF8.GetBytes(html);
        var header = $"HTTP/1.1 {statusCode} {statusText}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {htmlBytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        await stream.WriteAsync(htmlBytes, ct);
        await stream.FlushAsync(ct);
    }

    private async Task<string> ResolveClientSecretAsync(CancellationToken ct)
    {
        var dpapiSecret = await _credentialStore.LoadClientSecretAsync(ct);
        if (!string.IsNullOrWhiteSpace(dpapiSecret))
            return dpapiSecret;
        return _config.OAuthClient.ClientSecret ?? string.Empty;
    }

    private async Task<GoogleAuthTokens> ExchangeCodeForTokensAsync(string code, string verifier, string redirectUri, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _config.OAuthClient.ClientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri
        };
        var clientSecret = await ResolveClientSecretAsync(ct);
        if (!string.IsNullOrWhiteSpace(clientSecret))
            form["client_secret"] = clientSecret;

        using var resp = await _http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"OAuth token exchange failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var access = root.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("Missing access_token");
        var refresh = root.TryGetProperty("refresh_token", out var rEl) ? (rEl.GetString() ?? string.Empty) : string.Empty;
        var expiresIn = root.TryGetProperty("expires_in", out var eEl) ? eEl.GetInt64() : 3600;
        var expMs = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60)).ToUnixTimeMilliseconds();

        var (email, name) = await FetchUserInfoAsync(access, ct);
        return new GoogleAuthTokens(access, refresh, expMs, email, name);
    }

    private async Task<(string Email, string Name)> FetchUserInfoAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v2/userinfo");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return ("Connected Account", string.Empty);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var email = doc.RootElement.TryGetProperty("email", out var em) ? (em.GetString() ?? "") : "";
            var name = doc.RootElement.TryGetProperty("name", out var nm) ? (nm.GetString() ?? "") : "";
            return (email, name);
        }
        catch
        {
            return ("Connected Account", string.Empty);
        }
    }

    private async Task<GoogleAuthTokens?> EnsureValidTokenAsync(CancellationToken ct)
    {
        var tokens = await _credentialStore.LoadAsync(ct);
        if (tokens is null)
            return null;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (tokens.ExpiresAtUnixMs > now && !string.IsNullOrEmpty(tokens.AccessToken))
            return tokens;

        if (string.IsNullOrEmpty(tokens.RefreshToken) || !HasOAuthClientConfigured)
            return tokens;

        var form = new Dictionary<string, string>
        {
            ["client_id"] = _config.OAuthClient.ClientId,
            ["refresh_token"] = tokens.RefreshToken,
            ["grant_type"] = "refresh_token"
        };
        var clientSecret = await ResolveClientSecretAsync(ct);
        if (!string.IsNullOrWhiteSpace(clientSecret))
            form["client_secret"] = clientSecret;

        using var resp = await _http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Failed to refresh Google OAuth token ({(int)resp.StatusCode}). Please reconnect.");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var access = doc.RootElement.GetProperty("access_token").GetString() ?? tokens.AccessToken;
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var eEl) ? eEl.GetInt64() : 3600;
        var updated = tokens with
        {
            AccessToken = access,
            ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60)).ToUnixTimeMilliseconds()
        };
        await _credentialStore.SaveAsync(updated, ct);
        return updated;
    }

    public async Task<DisconnectResult> DisconnectAsync(CancellationToken ct = default)
    {
        bool remoteRevoked = false;
        string? remoteError = null;
        try
        {
            var tokens = await _credentialStore.LoadAsync(ct);
            if (tokens is not null)
            {
                var tokenToRevoke = !string.IsNullOrEmpty(tokens.RefreshToken) ? tokens.RefreshToken : tokens.AccessToken;
                if (!string.IsNullOrEmpty(tokenToRevoke))
                {
                    try
                    {
                        using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("token", tokenToRevoke) });
                        using var resp = await _http.PostAsync("https://oauth2.googleapis.com/revoke", content, ct);
                        if (resp.IsSuccessStatusCode)
                        {
                            remoteRevoked = true;
                        }
                        else
                        {
                            remoteError = $"Remote revocation returned HTTP {(int)resp.StatusCode}";
                        }
                    }
                    catch (Exception ex)
                    {
                        remoteError = $"Remote revocation failed: {ex.Message}";
                    }
                }
                else
                {
                    remoteRevoked = true;
                }
            }
            else
            {
                remoteRevoked = true;
            }
        }
        finally
        {
            await _credentialStore.ClearAsync(ct);
            _config.DriveRootFolderId = null;
            AuthStageChanged?.Invoke(OAuthConnectionStage.Idle, "Disconnected");
        }

        return new DisconnectResult(
            LocalCredentialsCleared: true,
            RemoteRevocationSucceeded: remoteRevoked,
            RemoteError: remoteError);
    }

    public async Task<DriveSyncReport> SyncPendingAsync(
        IProgress<DriveSyncProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!_config.DriveEnabled)
            return new DriveSyncReport(0, 0, 0, false, "Google Drive synchronization is disabled in Settings.");
        if (_config.PrimaryCloudProvider == CloudProviderKind.LocalOnly)
            return new DriveSyncReport(0, 0, 0, true, "Local-Only mode is active; Google Drive upload skipped.");
        if (_config.SyncPausedManually)
            return new DriveSyncReport(0, 0, 0, true, "Synchronization is paused manually.");

        await _syncLock.WaitAsync(ct);
        try
        {
            GoogleAuthTokens? tokens;
            try
            {
                tokens = await EnsureValidTokenAsync(ct);
            }
            catch (Exception ex)
            {
                if (_notifications is not null)
                    await _notifications.NotifyAsync("cloud", "Error", "GhostCode — Google Drive Authorization Lost", ex.Message, "gdrive_auth_lost", "open", ct);
                return new DriveSyncReport(0, 0, 0, false, ex.Message);
            }

            if (tokens is null)
                return new DriveSyncReport(0, 0, 0, false, "Not connected. Click 'Connect Google Account' in the Cloud tab.");

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var items = await _store.GetPendingOrRetryableUploadsAsync(nowMs, ct);
            if (items.Count == 0)
                return new DriveSyncReport(0, 0, 0, true, null);

            string folderId;
            try
            {
                folderId = await EnsureRemoteFolderIdAsync(tokens.AccessToken, ct);
            }
            catch (Exception ex)
            {
                return new DriveSyncReport(0, items.Count, 0, true, $"Cannot access Drive folder '{_config.DriveFolderName}': {ex.Message}");
            }

            int uploaded = 0, failed = 0, deduped = 0;
            string? lastError = null;

            for (int i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = items[i];
                progress?.Report(new DriveSyncProgress(i, items.Count, item.Version.FileName, (double)i / items.Count * 100.0));

                try
                {
                    if (!File.Exists(item.Version.ArchivePath))
                    {
                        await _store.MarkUploadAttemptAsync(item.Version.Id, UploadStatus.Failed, null, "Archived file missing on disk", ComputeNextRetry(item.Queue.AttemptCount), ct);
                        failed++;
                        continue;
                    }

                    // Duplicate avoidance during crash recovery
                    var existingId = await FindExistingDriveFileByShaAsync(tokens.AccessToken, folderId, item.Version.Sha256, ct);
                    if (!string.IsNullOrEmpty(existingId))
                    {
                        await _store.MarkUploadAttemptAsync(item.Version.Id, UploadStatus.Synced, existingId, null, 0, ct);
                        deduped++;
                        uploaded++;
                        continue;
                    }

                    var rawBytes = await File.ReadAllBytesAsync(item.Version.ArchivePath, ct);
                    var driveId = await UploadMultipartAsync(tokens.AccessToken, folderId, item.Version, rawBytes, ct);
                    if (string.IsNullOrEmpty(driveId))
                        throw new InvalidOperationException("Google Drive did not confirm file ID in response.");

                    await _store.MarkUploadAttemptAsync(item.Version.Id, UploadStatus.Synced, driveId, null, 0, ct);
                    uploaded++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    var nextRetry = ComputeNextRetry(item.Queue.AttemptCount);
                    await _store.MarkUploadAttemptAsync(item.Version.Id, UploadStatus.Failed, null, ex.Message, nextRetry, ct);
                    failed++;
                }
            }

            progress?.Report(new DriveSyncProgress(items.Count, items.Count, string.Empty, 100.0));
            if (_notifications is not null)
            {
                await _notifications.NotifyRepeatedFailureOrRecoveryAsync(
                    "cloud",
                    "gdrive_upload",
                    failed > 0,
                    "GhostCode — Google Drive Sync Interrupted",
                    $"Cannot upload {failed} file(s) to Google Drive. Local backups remain safe and queued ({lastError}).",
                    "GhostCode — Google Drive Sync Recovered",
                    $"Successfully uploaded {uploaded} queued file(s) to Google Drive.",
                    thresholdBeforeAlert: 2,
                    ct: ct);
            }
            var report = new DriveSyncReport(uploaded, failed, deduped, true, lastError);
            SyncCompleted?.Invoke(report);
            return report;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private static long ComputeNextRetry(int previousAttempts)
    {
        int exp = Math.Clamp(previousAttempts + 1, 1, 10);
        int backoffSec = Math.Min(900, 1 << exp);
        return DateTimeOffset.UtcNow.AddSeconds(backoffSec).ToUnixTimeMilliseconds();
    }

    private async Task<string> EnsureRemoteFolderIdAsync(string accessToken, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_config.DriveRootFolderId))
            return _config.DriveRootFolderId;

        var escaped = _config.DriveFolderName.Replace("'", "\\'");
        var q = $"name='{escaped}' and mimeType='application/vnd.google-apps.folder' and trashed=false";
        var searchUrl = $"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(q)}&fields=files(id,name)&pageSize=1";

        using var req = new HttpRequestMessage(HttpMethod.Get, searchUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Drive folder search failed ({(int)resp.StatusCode}): {body}");

        using (var doc = JsonDocument.Parse(body))
        {
            if (doc.RootElement.TryGetProperty("files", out var files) && files.GetArrayLength() > 0)
            {
                var found = files[0].GetProperty("id").GetString();
                if (!string.IsNullOrEmpty(found))
                {
                    _config.DriveRootFolderId = found;
                    return found;
                }
            }
        }

        // Create CodeArchive folder
        var createJson = JsonSerializer.Serialize(new
        {
            name = _config.DriveFolderName,
            mimeType = "application/vnd.google-apps.folder"
        });
        using var createReq = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/drive/v3/files?fields=id");
        createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        createReq.Content = new StringContent(createJson, Encoding.UTF8, "application/json");
        using var createResp = await _http.SendAsync(createReq, ct);
        var createBody = await createResp.Content.ReadAsStringAsync(ct);
        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Drive mkdir failed ({(int)createResp.StatusCode}): {createBody}");

        using var createdDoc = JsonDocument.Parse(createBody);
        var createdId = createdDoc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Missing folder id from Drive");
        _config.DriveRootFolderId = createdId;
        return createdId;
    }

    private async Task<string?> FindExistingDriveFileByShaAsync(string accessToken, string folderId, string sha256, CancellationToken ct)
    {
        var q = $"'{folderId}' in parents and trashed=false and appProperties has {{ key='ghostcode_sha256' and value='{sha256}' }}";
        var url = $"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(q)}&fields=files(id)&pageSize=1";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("files", out var files) && files.GetArrayLength() > 0)
            return files[0].GetProperty("id").GetString();
        return null;
    }

    private const string ManifestFileName = "ghostcode_archive_manifest.v2.json";

    private async Task<string> UploadMultipartAsync(
        string accessToken,
        string folderId,
        ArchiveVersionRecord version,
        byte[] rawBytes,
        CancellationToken ct)
    {
        var descJson = JsonSerializer.Serialize(new
        {
            gc = 2,
            id = version.Id,
            v = version.VersionNum,
            sp = version.SourcePath,
            fn = version.FileName,
            sha = version.Sha256,
            nh = version.NormHash,
            sz = version.SizeBytes,
            pc = version.ProblemCode,
            pt = version.ProblemTitle,
            pl = version.Platform,
            ts = version.CreatedAtUnixMs
        });
        if (descJson.Length > 1000)
            descJson = descJson[..1000];

        var metadata = JsonSerializer.Serialize(new
        {
            name = version.FileName,
            parents = new[] { folderId },
            description = descJson,
            appProperties = new Dictionary<string, string>
            {
                ["ghostcode_id"] = version.Id,
                ["ghostcode_sha256"] = version.Sha256,
                ["ghostcode_platform"] = version.Platform,
                ["ghostcode_version"] = version.VersionNum.ToString(),
                ["ghostcode_code"] = (version.ProblemCode ?? string.Empty).Length > 60 ? version.ProblemCode![..60] : (version.ProblemCode ?? string.Empty)
            }
        });

        using var multipart = new MultipartContent("related", "gc_" + Guid.NewGuid().ToString("N"));

        var metaPart = new StringContent(metadata, Encoding.UTF8, "application/json");
        var filePart = new ByteArrayContent(rawBytes);
        filePart.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        multipart.Add(metaPart);
        multipart.Add(filePart);

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id,name,sha256Checksum,size");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = multipart;

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Drive upload failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("sha256Checksum", out var shaProp))
        {
            var remoteSha = shaProp.GetString();
            if (!string.IsNullOrWhiteSpace(remoteSha)
                && !string.Equals(remoteSha, version.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Google Drive SHA-256 mismatch: expected {version.Sha256}, remote returned {remoteSha}.");
            }
        }
        return doc.RootElement.GetProperty("id").GetString() ?? string.Empty;
    }

    public async Task<bool> PublishManifestAsync(CancellationToken ct = default)
    {
        if (!_config.DriveEnabled)
            return false;

        await _syncLock.WaitAsync(ct);
        try
        {
            var tokens = await EnsureValidTokenAsync(ct);
            if (tokens is null)
                return false;
            var folderId = await EnsureRemoteFolderIdAsync(tokens.AccessToken, ct);
            await PublishManifestLockedAsync(tokens.AccessToken, folderId, ct);
            return true;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task PublishManifestLockedAsync(string accessToken, string folderId, CancellationToken ct)
    {
        var payloadBytes = await CloudManifestCodec.BuildManifestBytesAsync(_store, ct);
        var existingManifestId = await FindDriveFileByNameAsync(accessToken, folderId, ManifestFileName, ct);

        using var multipart = new MultipartContent("related", "gcm_" + Guid.NewGuid().ToString("N"));
        var meta = existingManifestId is null
            ? JsonSerializer.Serialize(new { name = ManifestFileName, parents = new[] { folderId }, mimeType = "application/json" })
            : JsonSerializer.Serialize(new { name = ManifestFileName, mimeType = "application/json" });
        multipart.Add(new StringContent(meta, Encoding.UTF8, "application/json"));
        var bodyPart = new ByteArrayContent(payloadBytes);
        bodyPart.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        multipart.Add(bodyPart);

        var method = existingManifestId is null ? HttpMethod.Post : HttpMethod.Patch;
        var uploadUrl = existingManifestId is null
            ? "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id"
            : $"https://www.googleapis.com/upload/drive/v3/files/{Uri.EscapeDataString(existingManifestId)}?uploadType=multipart&fields=id";

        using var req = new HttpRequestMessage(method, uploadUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = multipart;
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Manifest publish failed ({(int)resp.StatusCode}): {err}");
        }
    }

    public async Task<DrivePullReport> PullAndReconcileFromDriveAsync(
        IProgress<DriveSyncProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!_config.DriveEnabled)
            return new DrivePullReport(0, 0, 0, 0, false, "Google Drive synchronization is disabled in Settings.");

        await _syncLock.WaitAsync(ct);
        try
        {
            GoogleAuthTokens? tokens;
            try
            {
                tokens = await EnsureValidTokenAsync(ct);
            }
            catch (Exception ex)
            {
                return new DrivePullReport(0, 0, 0, 0, false, ex.Message);
            }

            if (tokens is null)
                return new DrivePullReport(0, 0, 0, 0, false, "Not connected. Click 'Connect Google Account' in the Cloud tab.");

            return await PullAndReconcileLockedAsync(tokens.AccessToken, progress, ct);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task<DrivePullReport> PullAndReconcileLockedAsync(
        string accessToken,
        IProgress<DriveSyncProgress>? progress,
        CancellationToken ct)
    {
        string folderId;
        try
        {
            folderId = await EnsureRemoteFolderIdAsync(accessToken, ct);
        }
        catch (Exception ex)
        {
            return new DrivePullReport(0, 0, 0, 0, true, $"Cannot access Drive folder '{_config.DriveFolderName}': {ex.Message}");
        }

        var remoteFiles = await ListFolderFilesAsync(accessToken, folderId, ct);

        int discovered = 0, newCloud = 0, conflicts = 0, eventsImported = 0;
        var seenDriveIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Reconcile manifest first if present (contains complete multi-version tree + statements + samples + submissions + history events)
        var manifestEntry = remoteFiles.FirstOrDefault(f => f.Name.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase));
        if (manifestEntry is not null)
        {
            var manifestBytes = await DownloadRawFileBytesAsync(accessToken, manifestEntry.Id, ct);
            var manifestRec = await CloudManifestCodec.ReconcileManifestBytesAsync(_store, manifestBytes, ct);
            discovered += manifestRec.Discovered;
            newCloud += manifestRec.NewCloudOnly;
            conflicts += manifestRec.Conflicts;
            eventsImported += manifestRec.EventsImported;
            foreach (var sid in manifestRec.SeenRemoteIds)
                seenDriveIds.Add(sid);
        }

        // 2. Also inspect individual remote files (handles uploads made without a manifest)
        int processed = 0;
        var codeFiles = remoteFiles.Where(f => !f.Name.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var rf in codeFiles)
        {
            ct.ThrowIfCancellationRequested();
            processed++;
            progress?.Report(new DriveSyncProgress(processed, Math.Max(1, codeFiles.Count), rf.Name, processed * 100.0 / Math.Max(1, codeFiles.Count)));

            var rec = ParseDriveFileMetadata(rf);
            if (rec is null)
                continue;
            discovered++;
            if (seenDriveIds.Contains(rf.Id))
                continue;

            var afterLookup = await _store.GetVersionByIdAsync(rec.Id, ct);
            var conflictBefore = (await _store.QueryVersionsAsync(limit: 10_000, ct: ct)).Count(x => x.IsConflictBranch);
            await _store.UpsertRemoteVersionMetadataAsync(rec, ct);
            if (afterLookup is null)
            {
                var updated = await _store.GetVersionByIdAsync(rec.Id, ct);
                if (updated is not null && !updated.IsLocalAvailable)
                    newCloud++;
            }
            var conflictAfter = (await _store.QueryVersionsAsync(limit: 10_000, ct: ct)).Count(x => x.IsConflictBranch);
            if (conflictAfter > conflictBefore)
                conflicts++;
        }

        progress?.Report(new DriveSyncProgress(codeFiles.Count, codeFiles.Count, string.Empty, 100.0));
        return new DrivePullReport(discovered, newCloud, conflicts, eventsImported, true, null);
    }

    public async Task<DriveDownloadResult> DownloadVersionAsync(string versionId, CancellationToken ct = default)
    {
        var version = await _store.GetVersionByIdAsync(versionId, ct);
        if (version is null)
            return new DriveDownloadResult(false, null, $"Archive version '{versionId}' was not found.");

        if (version.IsLocalAvailable && File.Exists(version.ArchivePath))
            return new DriveDownloadResult(true, version.ArchivePath, null);

        if (string.IsNullOrWhiteSpace(version.DriveFileId))
            return new DriveDownloadResult(false, null, $"Version '{version.FileName}' does not have a Google Drive file ID.");

        var tokens = await EnsureValidTokenAsync(ct);
        if (tokens is null)
            return new DriveDownloadResult(false, null, "Not connected to Google Drive.");

        byte[] bytes;
        try
        {
            bytes = await DownloadRawFileBytesAsync(tokens.AccessToken, version.DriveFileId, ct);
        }
        catch (Exception ex)
        {
            return new DriveDownloadResult(false, null, ex.Message);
        }

        var actualSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(version.Sha256)
            && !string.Equals(actualSha, version.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new DriveDownloadResult(false, null, $"Downloaded SHA-256 mismatch for {version.FileName}: expected {version.Sha256}, got {actualSha}.");
        }

        var cleanName = SourceAnalyzer.StripLegacyVersionAndHashSuffix(version.FileName);
        var shortHash = actualSha[..Math.Min(8, actualSha.Length)];
        var dateFolder = DateTimeOffset.FromUnixTimeMilliseconds(version.CreatedAtUnixMs).ToLocalTime().ToString("yyyy-MM-dd");

        var (archivePath, _) = await LosslessArchiver.WriteCleanHistoricalFileAsync(
            _config.GetInternalHistoryRoot(),
            dateFolder,
            version.VersionNum,
            shortHash,
            version.Id,
            cleanName,
            bytes,
            ct);

        await _store.MarkVersionDownloadedLocallyAsync(version.Id, archivePath, ct);
        return new DriveDownloadResult(true, archivePath, null);
    }

    public async Task<(int DownloadedCount, int FailedCount, string? LastError)> DownloadAllCloudOnlyVersionsAsync(
        IProgress<DriveSyncProgress>? progress = null,
        CancellationToken ct = default)
    {
        var all = await _store.QueryVersionsAsync(limit: 10_000, ct: ct);
        var pending = all.Where(v => !v.IsLocalAvailable && !string.IsNullOrWhiteSpace(v.DriveFileId)).ToList();
        int ok = 0, failed = 0;
        string? lastError = null;
        for (int i = 0; i < pending.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new DriveSyncProgress(i, pending.Count, pending[i].FileName, i * 100.0 / pending.Count));
            var res = await DownloadVersionAsync(pending[i].Id, ct);
            if (res.Success) ok++;
            else { failed++; lastError = res.Error; }
        }
        progress?.Report(new DriveSyncProgress(pending.Count, pending.Count, string.Empty, 100.0));
        return (ok, failed, lastError);
    }

    public async Task<(DriveSyncReport PushReport, DrivePullReport PullReport)> SyncBidirectionalAsync(
        IProgress<DriveSyncProgress>? progress = null,
        CancellationToken ct = default)
    {
        var push = await SyncPendingAsync(progress, ct);
        if (!push.Connected)
            return (push, new DrivePullReport(0, 0, 0, 0, false, push.LastError));

        await PublishManifestAsync(ct);
        var pull = await PullAndReconcileFromDriveAsync(progress, ct);
        return (push, pull);
    }

    private sealed record RemoteDriveFileEntry(
        string Id,
        string Name,
        long Size,
        string? Sha256Checksum,
        long CreatedUnixMs,
        string? Description,
        Dictionary<string, string> AppProperties);

    private async Task<string?> FindDriveFileByNameAsync(string accessToken, string folderId, string fileName, CancellationToken ct)
    {
        var escaped = fileName.Replace("'", "\\'");
        var q = $"'{folderId}' in parents and name='{escaped}' and trashed=false";
        var url = $"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(q)}&fields=files(id)&pageSize=1";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("files", out var files) && files.GetArrayLength() > 0)
            return files[0].GetProperty("id").GetString();
        return null;
    }

    private async Task<List<RemoteDriveFileEntry>> ListFolderFilesAsync(string accessToken, string folderId, CancellationToken ct)
    {
        var results = new List<RemoteDriveFileEntry>();
        string? pageToken = null;
        do
        {
            var q = $"'{folderId}' in parents and trashed=false and mimeType != 'application/vnd.google-apps.folder'";
            var url = $"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(q)}&fields=nextPageToken,files(id,name,size,sha256Checksum,createdTime,description,appProperties)&pageSize=200";
            if (!string.IsNullOrEmpty(pageToken))
                url += $"&pageToken={Uri.EscapeDataString(pageToken)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Drive file list failed ({(int)resp.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var pt) ? pt.GetString() : null;
            if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var f in files.EnumerateArray())
            {
                var id = f.GetProperty("id").GetString() ?? string.Empty;
                var name = f.TryGetProperty("name", out var np) ? (np.GetString() ?? string.Empty) : string.Empty;
                long size = 0;
                if (f.TryGetProperty("size", out var sp))
                {
                    if (sp.ValueKind == JsonValueKind.Number) size = sp.GetInt64();
                    else if (sp.ValueKind == JsonValueKind.String) long.TryParse(sp.GetString(), out size);
                }
                var sha = f.TryGetProperty("sha256Checksum", out var shp) ? shp.GetString() : null;
                long createdMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (f.TryGetProperty("createdTime", out var ctp) && DateTimeOffset.TryParse(ctp.GetString(), out var dto))
                    createdMs = dto.ToUnixTimeMilliseconds();
                var desc = f.TryGetProperty("description", out var dp) ? dp.GetString() : null;
                var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (f.TryGetProperty("appProperties", out var ap) && ap.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in ap.EnumerateObject())
                    {
                        var val = p.Value.GetString();
                        if (val is not null) props[p.Name] = val;
                    }
                }
                if (!string.IsNullOrEmpty(id))
                    results.Add(new RemoteDriveFileEntry(id, name, size, sha, createdMs, desc, props));
            }
        }
        while (!string.IsNullOrEmpty(pageToken));
        return results;
    }

    private async Task<byte[]> DownloadRawFileBytesAsync(string accessToken, string fileId, CancellationToken ct)
    {
        var url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?alt=media";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Drive download failed ({(int)resp.StatusCode}): {err}");
        }
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    private static ArchiveVersionRecord? ParseDriveFileMetadata(RemoteDriveFileEntry rf)
    {
        if (!string.IsNullOrWhiteSpace(rf.Description) && rf.Description.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(rf.Description);
                var root = doc.RootElement;
                if (root.TryGetProperty("gc", out _))
                {
                    var id = root.TryGetProperty("id", out var ip) ? (ip.GetString() ?? rf.Id) : rf.Id;
                    var ver = root.TryGetProperty("v", out var vp) ? vp.GetInt32() : 1;
                    var sp = root.TryGetProperty("sp", out var spp) ? (spp.GetString() ?? rf.Name) : rf.Name;
                    var fn = root.TryGetProperty("fn", out var fnp) ? (fnp.GetString() ?? rf.Name) : rf.Name;
                    var sha = root.TryGetProperty("sha", out var shp) ? (shp.GetString() ?? rf.Sha256Checksum ?? string.Empty) : (rf.Sha256Checksum ?? string.Empty);
                    var nh = root.TryGetProperty("nh", out var nhp) ? (nhp.GetString() ?? sha) : sha;
                    var sz = root.TryGetProperty("sz", out var szp) ? szp.GetInt64() : rf.Size;
                    var pc = root.TryGetProperty("pc", out var pcp) ? pcp.GetString() : null;
                    var pt = root.TryGetProperty("pt", out var ptp) ? ptp.GetString() : null;
                    var pl = root.TryGetProperty("pl", out var plp) ? (plp.GetString() ?? "Unknown") : "Unknown";
                    var ts = root.TryGetProperty("ts", out var tsp) ? tsp.GetInt64() : rf.CreatedUnixMs;
                    return new ArchiveVersionRecord(
                        id, ver, sp, $"cloud://{rf.Id}", null, fn, sha, nh, sz, pc, pt, pl, ts,
                        IsLocalAvailable: false, DriveFileId: rf.Id, IsConflictBranch: false);
                }
            }
            catch { }
        }

        if (rf.AppProperties.TryGetValue("ghostcode_sha256", out var propSha) && !string.IsNullOrWhiteSpace(propSha))
        {
            var id = rf.AppProperties.TryGetValue("ghostcode_id", out var gid) && !string.IsNullOrWhiteSpace(gid) ? gid : $"drive_{rf.Id}";
            int ver = rf.AppProperties.TryGetValue("ghostcode_version", out var gv) && int.TryParse(gv, out var parsedV) ? parsedV : 1;
            var plat = rf.AppProperties.TryGetValue("ghostcode_platform", out var gp) ? gp : "Unknown";
            rf.AppProperties.TryGetValue("ghostcode_code", out var code);
            return new ArchiveVersionRecord(
                id, ver, rf.Name, $"cloud://{rf.Id}", null, rf.Name, propSha, propSha, rf.Size, code, null, plat, rf.CreatedUnixMs,
                IsLocalAvailable: false, DriveFileId: rf.Id, IsConflictBranch: false);
        }

        return null;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var q = query.TrimStart('?');
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0) continue;
            var k = Uri.UnescapeDataString(pair[..idx]);
            var v = Uri.UnescapeDataString(pair[(idx + 1)..]);
            dict[k] = v;
        }
        return dict;
    }

    public async Task StopAsync()
    {
        var cts = _bgCts;
        if (cts is null)
            return;
        _bgCts = null;
        cts.Cancel();
        if (_bgTask is not null)
        {
            try { await _bgTask; } catch { }
        }
        cts.Dispose();
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}

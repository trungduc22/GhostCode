using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GhostCode.Core.Models;
using GhostCode.Core.Notifications;
using GhostCode.Core.Storage;

namespace GhostCode.Core.Cloud;

/// <summary>
/// Microsoft OneDrive cloud archive provider using Microsoft Identity v2.0 OAuth 2.0 Desktop PKCE
/// and Microsoft Graph Special AppFolder (<c>/me/drive/special/approot:/CodeArchive</c>).
/// </summary>
public sealed class OneDriveService : ICloudArchiveProvider, IAsyncDisposable
{
    public const string DefaultScopes = "Files.ReadWrite.AppFolder offline_access User.Read";

    private readonly GhostCodeConfig _config;
    private readonly SqliteArchiveStore _store;
    private readonly DpapiCredentialStore _credentialStore;
    private readonly HttpClient _http;
    private readonly NotificationCoordinator? _notifications;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private CancellationTokenSource? _bgCts;
    private Task? _bgTask;

    public OneDriveService(
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

    public CloudProviderKind ProviderKind => CloudProviderKind.OneDrive;
    public string DisplayName => "Microsoft OneDrive";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config.OneDriveOAuthClient?.ClientId);

    public event Action<DriveSyncReport>? SyncCompleted;
    public event Action<OAuthConnectionStage, string>? AuthStageChanged;

    private string FolderName => string.IsNullOrWhiteSpace(_config.OneDriveFolderName) ? "CodeArchive" : _config.OneDriveFolderName.Trim();

    private string TenantSegment =>
        string.IsNullOrWhiteSpace(_config.OneDriveOAuthClient?.TenantId)
            ? "consumers"
            : _config.OneDriveOAuthClient.TenantId.Trim();

    public async Task<GoogleAuthTokens?> GetConnectedAccountAsync(CancellationToken ct = default) =>
        await _credentialStore.LoadAsync(ct);

    public async Task<CloudAccountInfo> GetAccountInfoAsync(CancellationToken ct = default)
    {
        var tok = await _credentialStore.LoadAsync(ct);
        return tok is null
            ? new CloudAccountInfo(CloudProviderKind.OneDrive, false, string.Empty, "Not connected")
            : new CloudAccountInfo(CloudProviderKind.OneDrive, true, tok.Email, tok.DisplayName);
    }

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
                    && _config.PrimaryCloudProvider == CloudProviderKind.OneDrive
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

    public async Task<GoogleAuthTokens> ConnectWithBrowserAsync(
        Action<string>? openBrowser = null,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Microsoft OAuth Client ID is not configured. Open OneDrive OAuth setup in the Cloud tab.");

        var (verifier, challenge, state) = CloudTransferHelpers.CreatePkceParameters();
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var redirectUri = $"http://127.0.0.1:{port}/callback/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        var tenant = Uri.EscapeDataString(TenantSegment);
        var authUrl =
            $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize" +
            $"?client_id={Uri.EscapeDataString(_config.OneDriveOAuthClient.ClientId.Trim())}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&response_mode=query" +
            $"&scope={Uri.EscapeDataString(DefaultScopes)}" +
            $"&code_challenge={Uri.EscapeDataString(challenge)}" +
            $"&code_challenge_method=S256" +
            $"&state={Uri.EscapeDataString(state)}";

        AuthStageChanged?.Invoke(OAuthConnectionStage.WaitingForBrowserCallback, "Waiting for Microsoft account sign-in...");
        if (openBrowser is not null)
            openBrowser(new Uri(authUrl).AbsoluteUri);
        else
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        using var reg = ct.Register(() => { try { listener.Stop(); } catch { } });
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

        HttpListenerContext httpCtx;
        try
        {
            httpCtx = await listener.GetContextAsync().WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            AuthStageChanged?.Invoke(OAuthConnectionStage.Failed, "Microsoft OAuth sign-in timed out or was cancelled.");
            throw new TimeoutException("Timed out waiting for Microsoft OAuth callback.");
        }

        var query = CloudTransferHelpers.ParseQueryString(httpCtx.Request.Url?.Query ?? string.Empty);
        var returnedState = query.GetValueOrDefault("state", string.Empty);
        var returnedError = query.GetValueOrDefault("error", string.Empty);
        var code = query.GetValueOrDefault("code", string.Empty);

        var okHtml = "<!doctype html><html><body style='font-family:Segoe UI,sans-serif;padding:32px'><h2>GhostCode OneDrive Sign-In</h2><p>Callback received. You can return to GhostCode.</p></body></html>";
        var htmlBytes = Encoding.UTF8.GetBytes(okHtml);
        httpCtx.Response.ContentType = "text/html; charset=utf-8";
        httpCtx.Response.ContentLength64 = htmlBytes.Length;
        await httpCtx.Response.OutputStream.WriteAsync(htmlBytes, ct);
        httpCtx.Response.Close();
        listener.Stop();

        if (!string.IsNullOrEmpty(returnedError))
        {
            AuthStageChanged?.Invoke(OAuthConnectionStage.Failed, $"Microsoft sign-in failed: {returnedError}");
            throw new InvalidOperationException($"Microsoft OAuth error: {returnedError}");
        }
        if (!string.Equals(returnedState, state, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(code))
        {
            AuthStageChanged?.Invoke(OAuthConnectionStage.Failed, "Invalid OAuth state or missing code.");
            throw new InvalidOperationException("OAuth state mismatch or missing authorization code.");
        }

        AuthStageChanged?.Invoke(OAuthConnectionStage.CallbackReceivedExchangingToken, "Exchanging Microsoft authorization code...");
        var tokens = await ExchangeCodeAsync(code, redirectUri, verifier, ct);
        await _credentialStore.SaveAsync(tokens, ct);
        AuthStageChanged?.Invoke(OAuthConnectionStage.Connected, $"Connected OneDrive as {tokens.Email}");
        return tokens;
    }

    private async Task<GoogleAuthTokens> ExchangeCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct)
    {
        var tokenUrl = $"https://login.microsoftonline.com/{Uri.EscapeDataString(TenantSegment)}/oauth2/v2.0/token";
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _config.OneDriveOAuthClient.ClientId.Trim(),
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
            ["scope"] = DefaultScopes
        };

        using var resp = await _http.PostAsync(tokenUrl, new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Microsoft token exchange failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var accessToken = root.GetProperty("access_token").GetString() ?? throw new InvalidOperationException("Missing access_token");
        var refreshToken = root.TryGetProperty("refresh_token", out var rt) ? (rt.GetString() ?? string.Empty) : string.Empty;
        var expiresIn = root.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 3600;
        var expiresAtMs = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn)).ToUnixTimeMilliseconds();

        var (email, name) = await FetchUserProfileAsync(accessToken, ct);
        return new GoogleAuthTokens(accessToken, refreshToken, expiresAtMs, email, name);
    }

    private async Task<GoogleAuthTokens?> EnsureValidTokenAsync(CancellationToken ct)
    {
        var current = await _credentialStore.LoadAsync(ct);
        if (current is null)
            return null;

        if (current.ExpiresAtUnixMs > DateTimeOffset.UtcNow.AddSeconds(60).ToUnixTimeMilliseconds())
            return current;

        if (string.IsNullOrWhiteSpace(current.RefreshToken) || !IsConfigured)
            return null;

        var tokenUrl = $"https://login.microsoftonline.com/{Uri.EscapeDataString(TenantSegment)}/oauth2/v2.0/token";
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _config.OneDriveOAuthClient.ClientId.Trim(),
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = current.RefreshToken,
            ["scope"] = DefaultScopes
        };

        using var resp = await _http.PostAsync(tokenUrl, new FormUrlEncodedContent(form), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                await _credentialStore.ClearAsync(ct);
            throw new InvalidOperationException($"Microsoft token refresh failed ({(int)resp.StatusCode}): {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var newAccess = root.GetProperty("access_token").GetString() ?? current.AccessToken;
        var newRefresh = root.TryGetProperty("refresh_token", out var rt) && !string.IsNullOrEmpty(rt.GetString())
            ? rt.GetString()!
            : current.RefreshToken;
        var expiresIn = root.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 3600;
        var updated = current with
        {
            AccessToken = newAccess,
            RefreshToken = newRefresh,
            ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn)).ToUnixTimeMilliseconds()
        };
        await _credentialStore.SaveAsync(updated, ct);
        return updated;
    }

    private async Task<(string Email, string DisplayName)> FetchUserProfileAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me?$select=displayName,mail,userPrincipalName");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return ("OneDrive User", "OneDrive Account");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var mail = root.TryGetProperty("mail", out var m) && !string.IsNullOrWhiteSpace(m.GetString())
                ? m.GetString()!
                : root.TryGetProperty("userPrincipalName", out var upn) ? (upn.GetString() ?? "OneDrive User") : "OneDrive User";
            var name = root.TryGetProperty("displayName", out var dn) && !string.IsNullOrWhiteSpace(dn.GetString())
                ? dn.GetString()!
                : mail;
            return (mail, name);
        }
        catch
        {
            return ("OneDrive User", "OneDrive Account");
        }
    }

    public async Task<DisconnectResult> DisconnectAsync(CancellationToken ct = default)
    {
        await _credentialStore.ClearAsync(ct);
        AuthStageChanged?.Invoke(OAuthConnectionStage.Idle, "Microsoft OneDrive disconnected (local encrypted tokens cleared).");
        return new DisconnectResult(LocalCredentialsCleared: true, RemoteRevocationSucceeded: true, RemoteError: null);
    }

    public async Task<DriveSyncReport> SyncPendingAsync(
        IProgress<DriveSyncProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!_config.DriveEnabled)
            return new DriveSyncReport(0, 0, 0, false, "Cloud synchronization is disabled in Settings.");
        if (_config.PrimaryCloudProvider == CloudProviderKind.LocalOnly)
            return new DriveSyncReport(0, 0, 0, true, "Local-Only mode is active; OneDrive upload skipped.");
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
                    await _notifications.NotifyAsync("cloud", "Error", "GhostCode — OneDrive Authorization Lost", ex.Message, "onedrive_auth_lost", "open", ct);
                return new DriveSyncReport(0, 0, 0, false, ex.Message);
            }

            if (tokens is null)
                return new DriveSyncReport(0, 0, 0, false, "Not connected. Click 'Connect OneDrive' in the Cloud tab.");

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var items = await _store.GetPendingOrRetryableUploadsAsync(nowMs, ct);
            if (items.Count == 0)
                return new DriveSyncReport(0, 0, 0, true, null);

            IReadOnlyList<CloudObjectMetadata> remoteExisting;
            try
            {
                remoteExisting = await ListSubfolderObjectsLockedAsync(tokens.AccessToken, "History", ct);
            }
            catch (Exception ex)
            {
                return new DriveSyncReport(0, items.Count, 0, true, $"Cannot access OneDrive AppFolder '{FolderName}': {ex.Message}");
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
                        await _store.MarkUploadAttemptAsync(item.Version.Id, UploadStatus.Failed, null, "Archived file missing on disk", CloudTransferHelpers.ComputeNextRetryUnixMs(item.Queue.AttemptCount), ct);
                        failed++;
                        continue;
                    }

                    var matchedRemote = FindExistingRemoteVersion(remoteExisting, item.Version);
                    if (matchedRemote is not null)
                    {
                        await _store.MarkUploadAttemptAsync(item.Version.Id, UploadStatus.Synced, matchedRemote.RemoteId, null, 0, ct);
                        deduped++;
                        uploaded++;
                        continue;
                    }

                    var rawBytes = await File.ReadAllBytesAsync(item.Version.ArchivePath, ct);
                    var remoteId = await UploadVersionBytesLockedAsync(tokens.AccessToken, item.Version, rawBytes, ct);
                    if (string.IsNullOrEmpty(remoteId))
                        throw new InvalidOperationException("OneDrive did not confirm item ID in upload response.");

                    await _store.MarkUploadAttemptAsync(item.Version.Id, UploadStatus.Synced, remoteId, null, 0, ct);
                    uploaded++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    var nextRetry = CloudTransferHelpers.ComputeNextRetryUnixMs(item.Queue.AttemptCount);
                    await _store.MarkUploadAttemptAsync(item.Version.Id, UploadStatus.Failed, null, ex.Message, nextRetry, ct);
                    failed++;
                }
            }

            progress?.Report(new DriveSyncProgress(items.Count, items.Count, string.Empty, 100.0));
            if (_notifications is not null)
            {
                if (failed > 0 && !string.IsNullOrEmpty(lastError))
                    await _notifications.NotifyAsync("cloud", "Warning", "GhostCode — OneDrive Sync Failed", lastError, "onedrive_sync_error", "open", ct);
                else if (uploaded > 0 && _config.AlertSuccessfulSync)
                    await _notifications.NotifyAsync("cloud", "Info", "GhostCode — OneDrive Synced", $"Synchronized {uploaded} file(s) to OneDrive AppFolder.", "onedrive_sync_recovered", "open", ct);
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

    public async Task<bool> PublishManifestAsync(CancellationToken ct = default)
    {
        var tokens = await EnsureValidTokenAsync(ct);
        if (tokens is null)
            return false;

        var bytes = await CloudManifestCodec.BuildManifestBytesAsync(_store, ct);
        var url = BuildAppRootContentUrl($"{FolderName}/{CloudManifestCodec.ManifestFileName}");
        var itemId = await PutContentAsync(tokens.AccessToken, url, bytes, "application/json", ct);
        return !string.IsNullOrWhiteSpace(itemId);
    }

    public async Task<DrivePullReport> PullAndReconcileAsync(
        IProgress<DriveSyncProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!_config.DriveEnabled)
            return new DrivePullReport(0, 0, 0, 0, false, "Cloud synchronization is disabled in Settings.");

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
                return new DrivePullReport(0, 0, 0, 0, false, "Not connected. Click 'Connect OneDrive' in the Cloud tab.");

            int discovered = 0, newCloud = 0, conflicts = 0, eventsImported = 0;

            var rootItems = await ListSubfolderObjectsLockedAsync(tokens.AccessToken, string.Empty, ct);
            var manifestItem = rootItems.FirstOrDefault(f => f.Name.Equals(CloudManifestCodec.ManifestFileName, StringComparison.OrdinalIgnoreCase));
            if (manifestItem is not null)
            {
                var manifestBytes = await DownloadRawFileBytesAsync(tokens.AccessToken, manifestItem.RemoteId, ct);
                var rec = await CloudManifestCodec.ReconcileManifestBytesAsync(_store, manifestBytes, ct);
                discovered += rec.Discovered;
                newCloud += rec.NewCloudOnly;
                conflicts += rec.Conflicts;
                eventsImported += rec.EventsImported;
            }

            progress?.Report(new DriveSyncProgress(1, 1, CloudManifestCodec.ManifestFileName, 100.0));
            return new DrivePullReport(discovered, newCloud, conflicts, eventsImported, true, null);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<DriveDownloadResult> DownloadVersionAsync(string versionId, CancellationToken ct = default)
    {
        var version = await _store.GetVersionByIdAsync(versionId, ct);
        if (version is null)
            return new DriveDownloadResult(false, null, $"Archive version '{versionId}' was not found.");

        if (version.IsLocalAvailable && File.Exists(version.ArchivePath))
            return new DriveDownloadResult(true, version.ArchivePath, null);

        if (string.IsNullOrWhiteSpace(version.DriveFileId))
            return new DriveDownloadResult(false, null, $"Version '{version.FileName}' does not have a OneDrive remote item ID.");

        var tokens = await EnsureValidTokenAsync(ct);
        if (tokens is null)
            return new DriveDownloadResult(false, null, "Not connected to OneDrive.");

        byte[] bytes;
        try
        {
            bytes = await DownloadRawFileBytesAsync(tokens.AccessToken, version.DriveFileId, ct);
        }
        catch (Exception ex)
        {
            return new DriveDownloadResult(false, null, ex.Message);
        }

        return await CloudTransferHelpers.VerifyAndPersistDownloadedVersionAsync(_config, _store, version, bytes, ct);
    }

    public Task<(int DownloadedCount, int FailedCount, string? LastError)> DownloadAllCloudOnlyVersionsAsync(
        IProgress<DriveSyncProgress>? progress = null,
        CancellationToken ct = default) =>
        CloudTransferHelpers.DownloadAllCloudOnlyVersionsAsync(this, _store, progress, ct);

    public async Task<(DriveSyncReport PushReport, DrivePullReport PullReport)> SyncBidirectionalAsync(
        IProgress<DriveSyncProgress>? progress = null,
        CancellationToken ct = default)
    {
        var push = await SyncPendingAsync(progress, ct);
        if (!push.Connected)
            return (push, new DrivePullReport(0, 0, 0, 0, false, push.LastError));

        await PublishManifestAsync(ct);
        var pull = await PullAndReconcileAsync(progress, ct);
        return (push, pull);
    }

    public async Task<IReadOnlyList<CloudObjectMetadata>> ListRemoteObjectsAsync(CancellationToken ct = default)
    {
        var tokens = await EnsureValidTokenAsync(ct);
        if (tokens is null)
            return Array.Empty<CloudObjectMetadata>();

        var rootItems = await ListSubfolderObjectsLockedAsync(tokens.AccessToken, string.Empty, ct);
        var historyItems = await ListSubfolderObjectsLockedAsync(tokens.AccessToken, "History", ct);
        var merged = new List<CloudObjectMetadata>(rootItems.Count + historyItems.Count);
        merged.AddRange(rootItems);
        merged.AddRange(historyItems);
        return merged;
    }

    public async Task<byte[]> DownloadRawBytesAsync(string remoteId, CancellationToken ct = default)
    {
        var tokens = await EnsureValidTokenAsync(ct)
            ?? throw new InvalidOperationException("Not connected to Microsoft OneDrive.");
        return await DownloadRawFileBytesAsync(tokens.AccessToken, remoteId, ct);
    }

    public async Task<string> UploadVersionBytesAsync(ArchiveVersionRecord version, byte[] rawBytes, CancellationToken ct = default)
    {
        var tokens = await EnsureValidTokenAsync(ct)
            ?? throw new InvalidOperationException("Not connected to Microsoft OneDrive.");
        return await UploadVersionBytesLockedAsync(tokens.AccessToken, version, rawBytes, ct);
    }

    private string BuildAppRootContentUrl(string relativePath)
    {
        var encodedSegments = string.Join(
            '/',
            relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        return $"https://graph.microsoft.com/v1.0/me/drive/special/approot:/{encodedSegments}:/content";
    }

    private async Task<string> UploadVersionBytesLockedAsync(
        string accessToken,
        ArchiveVersionRecord version,
        byte[] rawBytes,
        CancellationToken ct)
    {
        if (rawBytes.LongLength > 4L * 1024 * 1024)
            throw new InvalidOperationException("Source file exceeds Graph 4 MB single-PUT AppFolder limit.");

        var safeName = BuildRemoteVersionFileName(version);
        var url = BuildAppRootContentUrl($"{FolderName}/History/{safeName}");
        var itemId = await PutContentAsync(accessToken, url, rawBytes, "application/octet-stream", ct);
        if (string.IsNullOrWhiteSpace(itemId))
            throw new InvalidOperationException("Microsoft Graph upload did not return an item ID.");
        return itemId;
    }

    private static string BuildRemoteVersionFileName(ArchiveVersionRecord version)
    {
        var baseName = Path.GetFileName(version.FileName);
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "solution.txt";
        foreach (var bad in Path.GetInvalidFileNameChars())
            baseName = baseName.Replace(bad, '_');
        return $"{version.Id}_{baseName}";
    }

    private static CloudObjectMetadata? FindExistingRemoteVersion(
        IReadOnlyList<CloudObjectMetadata> remoteObjects,
        ArchiveVersionRecord version)
    {
        var expectedName = BuildRemoteVersionFileName(version);
        return remoteObjects.FirstOrDefault(r =>
            r.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(r.Sha256) && r.Sha256.Equals(version.Sha256, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<string> PutContentAsync(
        string accessToken,
        string url,
        byte[] bytes,
        string mediaType,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new ByteArrayContent(bytes);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"OneDrive upload failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("id", out var idProp) ? (idProp.GetString() ?? string.Empty) : string.Empty;
    }

    private async Task<List<CloudObjectMetadata>> ListSubfolderObjectsLockedAsync(
        string accessToken,
        string subfolder,
        CancellationToken ct)
    {
        var results = new List<CloudObjectMetadata>();
        var pathSegment = string.IsNullOrWhiteSpace(subfolder)
            ? Uri.EscapeDataString(FolderName)
            : $"{Uri.EscapeDataString(FolderName)}/{Uri.EscapeDataString(subfolder)}";
        string? nextUrl = $"https://graph.microsoft.com/v1.0/me/drive/special/approot:/{pathSegment}:/children";

        while (!string.IsNullOrEmpty(nextUrl))
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, nextUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await _http.SendAsync(req, ct);

            if (resp.StatusCode == HttpStatusCode.NotFound)
                return results;

            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"OneDrive list children failed ({(int)resp.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            nextUrl = doc.RootElement.TryGetProperty("@odata.nextLink", out var nl) ? nl.GetString() : null;

            if (!doc.RootElement.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.TryGetProperty("folder", out _))
                    continue;

                var id = item.TryGetProperty("id", out var ip) ? (ip.GetString() ?? string.Empty) : string.Empty;
                var name = item.TryGetProperty("name", out var np) ? (np.GetString() ?? string.Empty) : string.Empty;
                var size = item.TryGetProperty("size", out var sp) && sp.ValueKind == JsonValueKind.Number ? sp.GetInt64() : 0L;

                string? sha = null;
                if (item.TryGetProperty("file", out var fp)
                    && fp.TryGetProperty("hashes", out var hp)
                    && hp.TryGetProperty("sha256Hash", out var shp))
                {
                    sha = shp.GetString()?.ToLowerInvariant();
                }

                long modifiedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (item.TryGetProperty("lastModifiedDateTime", out var lm) && DateTimeOffset.TryParse(lm.GetString(), out var dto))
                    modifiedMs = dto.ToUnixTimeMilliseconds();

                if (!string.IsNullOrWhiteSpace(id))
                    results.Add(new CloudObjectMetadata(id, name, size, sha, modifiedMs));
            }
        }

        return results;
    }

    private async Task<byte[]> DownloadRawFileBytesAsync(string accessToken, string remoteId, CancellationToken ct)
    {
        var url = $"https://graph.microsoft.com/v1.0/me/drive/items/{Uri.EscapeDataString(remoteId)}/content";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"OneDrive download failed ({(int)resp.StatusCode}): {err}");
        }
        return await resp.Content.ReadAsByteArrayAsync(ct);
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

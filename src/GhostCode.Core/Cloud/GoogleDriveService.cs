using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GhostCode.Core.Models;
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

/// <summary>
/// Native Google Drive v3 client using OAuth 2.0 Desktop PKCE, DPAPI token persistence,
/// SHA-256 duplicate avoidance, bounded exponential backoff, and explicit server confirmation.
/// </summary>
public sealed class GoogleDriveService : IAsyncDisposable
{
    public const string DriveScope = "https://www.googleapis.com/auth/drive.file openid email profile";

    private readonly GhostCodeConfig _config;
    private readonly SqliteArchiveStore _store;
    private readonly DpapiCredentialStore _credentialStore;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private CancellationTokenSource? _bgCts;
    private Task? _bgTask;

    public GoogleDriveService(
        GhostCodeConfig config,
        SqliteArchiveStore store,
        DpapiCredentialStore credentialStore,
        HttpClient? httpClient = null)
    {
        _config = config;
        _store = store;
        _credentialStore = credentialStore;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    }

    public bool HasOAuthClientConfigured => !string.IsNullOrWhiteSpace(_config.OAuthClient?.ClientId);

    public event Action<DriveSyncReport>? SyncCompleted;

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
                if (_config.DriveEnabled && await GetConnectedAccountAsync(ct) is not null)
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
            throw new InvalidOperationException(
                "Google OAuth Desktop Client ID is not configured. Open Settings/Cloud OAuth setup and enter your Google Cloud Desktop Client ID.");
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

            string? authCode = await WaitForOAuthCallbackAsync(listener, state, ct);
            if (string.IsNullOrEmpty(authCode))
                throw new InvalidOperationException("Google authorization was cancelled or did not return an authorization code.");

            var tokens = await ExchangeCodeForTokensAsync(authCode, verifier, redirectUri, ct);
            await _credentialStore.SaveAsync(tokens, ct);
            return tokens;
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }

    private static async Task<string?> WaitForOAuthCallbackAsync(TcpListener listener, string expectedState, CancellationToken ct)
    {
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

        bool ok = string.Equals(gotState, expectedState, StringComparison.Ordinal) && !string.IsNullOrEmpty(code) && string.IsNullOrEmpty(error);
        var html = ok
            ? "<html><body style='font-family:Segoe UI,sans-serif;padding:40px'><h2>GhostCode connected to Google Drive!</h2><p>You can close this tab and return to GhostCode.</p></body></html>"
            : $"<html><body style='font-family:Segoe UI,sans-serif;padding:40px'><h2>Authorization failed</h2><p>{WebUtility.HtmlEncode(error ?? "State mismatch or cancelled")}</p></body></html>";
        var htmlBytes = Encoding.UTF8.GetBytes(html);
        var header = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {htmlBytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        await stream.WriteAsync(htmlBytes, ct);
        await stream.FlushAsync(ct);

        return ok ? code : null;
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
        if (!string.IsNullOrWhiteSpace(_config.OAuthClient.ClientSecret))
            form["client_secret"] = _config.OAuthClient.ClientSecret;

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
        if (!string.IsNullOrWhiteSpace(_config.OAuthClient.ClientSecret))
            form["client_secret"] = _config.OAuthClient.ClientSecret;

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

    public async Task DisconnectAsync(CancellationToken ct = default)
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
                    await _http.PostAsync("https://oauth2.googleapis.com/revoke", content, ct);
                }
                catch
                {
                    // Best-effort remote revocation; local DPAPI tokens are still cleared below
                }
            }
        }
        await _credentialStore.ClearAsync(ct);
        _config.DriveRootFolderId = null;
    }

    public async Task<DriveSyncReport> SyncPendingAsync(
        IProgress<DriveSyncProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!_config.DriveEnabled)
            return new DriveSyncReport(0, 0, 0, false, "Google Drive synchronization is disabled in Settings.");

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

    private async Task<string> UploadMultipartAsync(
        string accessToken,
        string folderId,
        ArchiveVersionRecord version,
        byte[] rawBytes,
        CancellationToken ct)
    {
        var metadata = JsonSerializer.Serialize(new
        {
            name = version.FileName,
            parents = new[] { folderId },
            appProperties = new Dictionary<string, string>
            {
                ["ghostcode_sha256"] = version.Sha256,
                ["ghostcode_platform"] = version.Platform,
                ["ghostcode_version"] = version.VersionNum.ToString()
            }
        });

        using var multipart = new MultipartContent("related", "gc_" + Guid.NewGuid().ToString("N"));

        var metaPart = new StringContent(metadata, Encoding.UTF8, "application/json");
        var filePart = new ByteArrayContent(rawBytes);
        filePart.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        multipart.Add(metaPart);
        multipart.Add(filePart);

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id,name");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = multipart;

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Drive upload failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetString() ?? string.Empty;
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

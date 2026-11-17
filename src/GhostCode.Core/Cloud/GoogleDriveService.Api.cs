using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GhostCode.Core.Models;
using GhostCode.Core.Notifications;

namespace GhostCode.Core.Cloud;

public sealed partial class GoogleDriveService
{
    private const string ManifestFileName = "ghostcode_archive_manifest.v2.json";

    private sealed record RemoteDriveFileEntry(
        string Id,
        string Name,
        long Size,
        string? Sha256Checksum,
        long CreatedUnixMs,
        string? Description,
        Dictionary<string, string> AppProperties);

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
            throw GoogleApiErrorParser.CreateException(resp.StatusCode, body, _config.OAuthClient?.ClientId);

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
            throw GoogleApiErrorParser.CreateException(createResp.StatusCode, createBody, _config.OAuthClient?.ClientId);

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
        multipart.Add(new StringContent(metadata, Encoding.UTF8, "application/json"));
        var filePart = new ByteArrayContent(rawBytes);
        filePart.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        multipart.Add(filePart);

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id,name,sha256Checksum,size");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = multipart;

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw GoogleApiErrorParser.CreateException(resp.StatusCode, body, _config.OAuthClient?.ClientId);

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
            throw GoogleApiErrorParser.CreateException(resp.StatusCode, err, _config.OAuthClient?.ClientId);
        }
    }

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
                throw GoogleApiErrorParser.CreateException(resp.StatusCode, body, _config.OAuthClient?.ClientId);

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
            throw GoogleApiErrorParser.CreateException(resp.StatusCode, err, _config.OAuthClient?.ClientId);
        }
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task<bool> DeleteDriveFileLockedAsync(string accessToken, string remoteId, CancellationToken ct)
    {
        var url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(remoteId)}";
        using var req = new HttpRequestMessage(HttpMethod.Delete, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound || resp.StatusCode == HttpStatusCode.Gone)
            return true;
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw GoogleApiErrorParser.CreateException(resp.StatusCode, err, _config.OAuthClient?.ClientId);
        }
        return true;
    }

    private CloudServiceErrorDetails ClassifyDriveException(Exception ex)
    {
        if (ex is CloudServiceException cex)
            return cex.Details;

        if (ex is TimeoutException or TaskCanceledException)
        {
            return new CloudServiceErrorDetails(
                ErrorCategory.ConnectionTimeout, "Google Drive", 0, "DEADLINE_EXCEEDED", "timeout", "drive.googleapis.com",
                ex.Message, GoogleApiErrorParser.ExtractProjectNumberFromClientId(_config.OAuthClient?.ClientId), null, null, string.Empty);
        }

        if (ex is HttpRequestException hre && hre.StatusCode is HttpStatusCode sc)
            return GoogleApiErrorParser.Parse(sc, ex.Message, _config.OAuthClient?.ClientId);

        if (ex is HttpRequestException or SocketException)
        {
            return new CloudServiceErrorDetails(
                ErrorCategory.NetworkOffline, "Google Drive", 0, "UNAVAILABLE", "networkUnavailable", "drive.googleapis.com",
                ex.Message, GoogleApiErrorParser.ExtractProjectNumberFromClientId(_config.OAuthClient?.ClientId), null, null, string.Empty);
        }

        var mapped = UserFriendlyErrorService.ClassifyAndMap(ex, _config.Language, ErrorCategory.PermissionDenied);
        return new CloudServiceErrorDetails(
            mapped.Category, "Google Drive", 0, string.Empty, string.Empty, "drive.googleapis.com",
            ex.Message, GoogleApiErrorParser.ExtractProjectNumberFromClientId(_config.OAuthClient?.ClientId), mapped.ActionUrl, null, string.Empty);
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
}

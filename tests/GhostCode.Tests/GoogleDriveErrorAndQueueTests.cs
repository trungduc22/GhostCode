using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using GhostCode.Core.Analysis;
using GhostCode.Core.Archiver;
using GhostCode.Core.Cloud;
using GhostCode.Core.Models;
using GhostCode.Core.Notifications;
using GhostCode.Core.Storage;
using Xunit;

namespace GhostCode.Tests;

public sealed class GoogleDriveErrorAndQueueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ghostcode_gdrive403_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private const string ExactServiceDisabled403Json = """
        {
          "error": {
            "code": 403,
            "message": "Google Drive API has not been used in project 1085876972632 before or it is disabled. Enable it by visiting https://console.developers.google.com/apis/api/drive.googleapis.com/overview?project=1085876972632 then retry.",
            "errors": [
              {
                "message": "Google Drive API has not been used in project 1085876972632 before or it is disabled.",
                "domain": "usageLimits",
                "reason": "accessNotConfigured",
                "extendedHelp": "https://console.developers.google.com"
              }
            ],
            "status": "PERMISSION_DENIED",
            "details": [
              {
                "@type": "type.googleapis.com/google.rpc.ErrorInfo",
                "reason": "SERVICE_DISABLED",
                "domain": "googleapis.com",
                "metadata": {
                  "service": "drive.googleapis.com",
                  "consumer": "projects/1085876972632",
                  "activationUrl": "https://console.developers.google.com/apis/api/drive.googleapis.com/overview?project=1085876972632"
                }
              }
            ]
          }
        }
        """;

    [Fact]
    public void GoogleApiErrorParser_ParsesServiceDisabled403AndMapsExactVietnameseError()
    {
        var parsed = GoogleApiErrorParser.Parse(
            HttpStatusCode.Forbidden,
            ExactServiceDisabled403Json,
            "1085876972632-abcdefg.apps.googleusercontent.com");

        Assert.Equal(ErrorCategory.CloudApiDisabled, parsed.Category);
        Assert.Equal(403, parsed.HttpStatusCode);
        Assert.Equal("PERMISSION_DENIED", parsed.ErrorStatus);
        Assert.Equal("SERVICE_DISABLED", parsed.Reason);
        Assert.Equal("drive.googleapis.com", parsed.ServiceName);
        Assert.Equal("1085876972632", parsed.ProjectNumber);
        Assert.True(parsed.OAuthProjectMatchesError);
        Assert.Equal(
            "https://console.developers.google.com/apis/api/drive.googleapis.com/overview?project=1085876972632",
            parsed.ValidatedActivationUrl);

        var friendly = UserFriendlyErrorService.MapError(parsed, UiLanguage.Vi);
        Assert.Equal("Google Drive API chưa được kích hoạt", friendly.Title);
        Assert.Contains("chưa được bật trong dự án Google Cloud đang sử dụng", friendly.Explanation, StringComparison.Ordinal);
        Assert.Equal("Bật Google Drive API", friendly.ActionLabel);
        Assert.Equal("open_google_drive_api", friendly.ActionKey);
        Assert.Equal(parsed.ValidatedActivationUrl, friendly.ActionUrl);
    }

    [Fact]
    public void GoogleApiErrorParser_RejectsUntrustedActivationUrlAndDetectsOAuthMismatch()
    {
        const string spoofedJson = """
            {
              "error": {
                "code": 403,
                "message": "Drive API disabled.",
                "status": "PERMISSION_DENIED",
                "details": [
                  {
                    "reason": "SERVICE_DISABLED",
                    "metadata": {
                      "service": "drive.googleapis.com",
                      "consumer": "projects/1085876972632",
                      "activationUrl": "https://evil-phishing.example.com/drive?project=1085876972632"
                    }
                  }
                ]
              }
            }
            """;

        var parsed = GoogleApiErrorParser.Parse(
            HttpStatusCode.Forbidden,
            spoofedJson,
            "999888777666-otherproject.apps.googleusercontent.com");

        Assert.Equal(ErrorCategory.CloudApiDisabled, parsed.Category);
        Assert.Equal("https://console.developers.google.com/apis/api/drive.googleapis.com/overview?project=1085876972632", parsed.ValidatedActivationUrl);
        Assert.False(parsed.OAuthProjectMatchesError);
        Assert.Contains("999888777666", parsed.OAuthProjectNote, StringComparison.Ordinal);
        Assert.Contains("1085876972632", parsed.OAuthProjectNote, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":{"code":401,"status":"UNAUTHENTICATED","message":"Invalid Credentials"}}""", ErrorCategory.AuthenticationExpired)]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"code":403,"errors":[{"reason":"storageQuotaExceeded"}]}}""", ErrorCategory.CloudQuotaExceeded)]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"code":403,"errors":[{"reason":"insufficientPermissions"}]}}""", ErrorCategory.CloudInsufficientPermissions)]
    [InlineData((HttpStatusCode)429, """{"error":{"code":429,"errors":[{"reason":"rateLimitExceeded"}]}}""", ErrorCategory.CloudRateLimited)]
    public void GoogleApiErrorParser_DistinguishesAuthQuotaPermissionsAndRateLimitWithoutFalseOffline(
        HttpStatusCode status,
        string body,
        ErrorCategory expected)
    {
        var parsed = GoogleApiErrorParser.Parse(status, body);
        Assert.Equal(expected, parsed.Category);
        Assert.NotEqual(ErrorCategory.NetworkOffline, parsed.Category);
    }

    [Fact]
    public async Task GoogleDriveService_ServiceDisabled403_PreservesQueueAndResumesCleanlyAfterEnable()
    {
        Directory.CreateDirectory(_root);
        var watchDir = Path.Combine(_root, "src");
        Directory.CreateDirectory(watchDir);

        var cfg = new GhostCodeConfig
        {
            ArchiveDir = Path.Combine(_root, "GhostCode"),
            ReadyDir = string.Empty,
            DatabasePath = Path.Combine(_root, "ghostcode.db"),
            PrimaryCloudProvider = CloudProviderKind.GoogleDrive,
            DriveEnabled = true,
            OAuthClient = new GoogleOAuthClientConfig { ClientId = "1085876972632-test.apps.googleusercontent.com" }
        };

        await using var store = new SqliteArchiveStore(cfg.DatabasePath);
        await store.InitializeAsync();
        var archiver = new LosslessArchiver(cfg, store);

        for (int i = 1; i <= 2; i++)
        {
            var p = Path.Combine(watchDir, $"sol{i}.cpp");
            await File.WriteAllTextAsync(p, $"#include <iostream>\nint main(){{ int x = {i * 11}; std::cout << x; return 0; }}\n");
            await archiver.ProcessFileSaveAsync(p, await File.ReadAllBytesAsync(p), null);
        }

        var credStore = new DpapiCredentialStore(Path.Combine(_root, "creds.dpapi"));
        await credStore.SaveAsync(new GoogleAuthTokens(
            "fake-access-token",
            "fake-refresh-token",
            DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
            "user@example.com",
            "Test User"));

        bool apiEnabled = false;
        int requestCountWhileDisabled = 0;
        var uploadedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var handler = new StubHttpHandler(async req =>
        {
            var url = req.RequestUri!.ToString();
            if (!apiEnabled)
            {
                requestCountWhileDisabled++;
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent(ExactServiceDisabled403Json, Encoding.UTF8, "application/json")
                };
            }

            var unescaped = Uri.UnescapeDataString(url);
            if (unescaped.Contains("mimeType='application/vnd.google-apps.folder'", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"files":[{"id":"folder-123","name":"CodeArchive"}]}""", Encoding.UTF8, "application/json")
                };

            if (url.Contains("ghostcode_sha256", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"files":[]}""", Encoding.UTF8, "application/json")
                };

            if (req.Method == HttpMethod.Post && url.Contains("/upload/drive/v3/files", StringComparison.Ordinal))
            {
                var driveId = $"drv_{Guid.NewGuid():N}";
                uploadedIds.Add(driveId);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { id = driveId, name = "file.cpp" }), Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"files":[]}""", Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        await using var drive = new GoogleDriveService(cfg, store, credStore, http);

        // 1. Trigger sync while API disabled
        var failReport = await drive.SyncPendingAsync();
        Assert.True(failReport.Connected);
        Assert.NotNull(failReport.ErrorDetails);
        Assert.Equal(ErrorCategory.CloudApiDisabled, failReport.ErrorDetails!.Category);
        Assert.Equal(1, requestCountWhileDisabled); // Failed fast on EnsureRemoteFolderIdAsync, never hammered per-file
        Assert.True(drive.IsAwaitingApiActivation);

        // Background sync check MUST respect cooldown instead of hammering API repeatedly
        await drive.TryBackgroundSyncOnceAsync();
        Assert.Equal(1, requestCountWhileDisabled);

        // Queue MUST preserve every pending upload intact cleanly!
        var queueAfterFail = await store.GetAllUploadQueueAsync(50);
        Assert.Equal(2, queueAfterFail.Count);
        Assert.All(queueAfterFail, q => Assert.Equal(UploadStatus.Pending, q.Queue.Status));

        // 2. Enable API remotely and manually retry WITHOUT restarting service
        apiEnabled = true;
        drive.ClearApiConfigurationPause();
        var okReport = await drive.SyncPendingAsync();
        Assert.Equal(2, okReport.UploadedCount);
        Assert.Equal(0, okReport.FailedCount);
        Assert.False(drive.IsAwaitingApiActivation);

        var queueAfterOk = await store.GetAllUploadQueueAsync(50);
        Assert.Equal(2, queueAfterOk.Count);
        Assert.All(queueAfterOk, q => Assert.Equal(UploadStatus.Synced, q.Queue.Status));
        Assert.Equal(2, uploadedIds.Count);
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request);
    }
}

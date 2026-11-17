using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using GhostCode.Core.Cloud;
using GhostCode.Core.Models;
using Microsoft.Data.Sqlite;

namespace GhostCode.Core.Notifications;

public enum ErrorCategory
{
    AuthenticationExpired,
    NetworkOffline,
    ConnectionTimeout,
    CloudQuotaExceeded,
    CloudApiDisabled,
    CloudInsufficientPermissions,
    CloudRateLimited,
    PermissionDenied,
    InvalidFilePath,
    WatchedFolderUnavailable,
    BrowserExtensionMissing,
    NativeMessagingFailure,
    BridgeAuthenticationFailure,
    CompilerMissing,
    CompilationError,
    RuntimeError,
    ExecutionTimeout,
    ArchiveIntegrityFailure,
    DatabaseError,
    CloudMigrationConflict
}

public sealed record FriendlyErrorInfo(
    ErrorCategory Category,
    string Title,
    string Explanation,
    string ActionLabel,
    string ActionKey,
    string RedactedTechnicalDetails,
    string? ActionUrl = null);

/// <summary>
/// Converts raw technical exceptions/errors across 17 domain categories into
/// human-friendly localized messages, recommended action hints, and secret-redacted details.
/// </summary>
public static partial class UserFriendlyErrorService
{
    public static FriendlyErrorInfo MapError(
        ErrorCategory category,
        string? rawTechnicalDetails,
        UiLanguage language)
    {
        bool vi = language == UiLanguage.Vi;
        var (title, explanation, actionLabel, actionKey) = category switch
        {
            ErrorCategory.AuthenticationExpired => vi
                ? ("Phiên đăng nhập đám mây đã hết hạn", "Tài khoản đám mây cần được đăng nhập lại để tiếp tục đồng bộ.", "Đăng nhập lại", "connect_cloud")
                : ("Cloud Sign-In Expired", "Your cloud account authorization expired and needs to be signed in again.", "Sign In Again", "connect_cloud"),

            ErrorCategory.NetworkOffline => vi
                ? ("Không có kết nối mạng", "GhostCode vẫn bảo vệ tệp cục bộ và sẽ tự đồng bộ khi có Internet trở lại.", "Thử lại", "retry_sync")
                : ("No Internet Connection", "GhostCode is still protecting your local files and will sync once back online.", "Retry", "retry_sync"),

            ErrorCategory.ConnectionTimeout => vi
                ? ("Kết nối đám mây bị quá thời gian", "Máy chủ đám mây phản hồi quá chậm; bạn có thể thử đồng bộ lại sau ít phút.", "Thử lại", "retry_sync")
                : ("Connection Timed Out", "The cloud server took too long to respond; please retry in a moment.", "Retry", "retry_sync"),

            ErrorCategory.CloudQuotaExceeded => vi
                ? ("Dung lượng đám mây đã đầy", "Kho lưu trữ đám mây không còn đủ dung lượng trống cho bản sao lưu mới.", "Mở cài đặt Drive", "open_cloud")
                : ("Cloud Storage Quota Full", "Your cloud drive does not have enough free space for new backups.", "Open Drive", "open_cloud"),

            ErrorCategory.CloudApiDisabled => vi
                ? ("Google Drive API chưa được kích hoạt", "GhostCode chưa thể đồng bộ mã nguồn vì Google Drive API chưa được bật trong dự án Google Cloud đang sử dụng. Các phiên bản vẫn được lưu cục bộ.", "Bật Google Drive API", "open_google_drive_api")
                : ("Google Drive API Not Enabled", "GhostCode cannot sync yet because Google Drive API is disabled in the active Google Cloud project. Local versions remain safely saved.", "Enable Google Drive API", "open_google_drive_api"),

            ErrorCategory.CloudInsufficientPermissions => vi
                ? ("Thiếu quyền truy cập Google Drive", "Tài khoản chưa cấp đủ quyền tệp cho GhostCode. Hãy ngắt kết nối và đăng nhập lại để cấp quyền.", "Đăng nhập lại", "connect_cloud")
                : ("Insufficient Google Drive Permissions", "The authorized session lacks Drive file permissions. Disconnect and sign in again to grant access.", "Sign In Again", "connect_cloud"),

            ErrorCategory.CloudRateLimited => vi
                ? ("Google Drive đang giới hạn tần suất", "Yêu cầu đồng bộ tạm thời vượt ngưỡng giới hạn của Google Drive; GhostCode sẽ tự thử lại sau.", "Thử lại", "retry_sync")
                : ("Google Drive Rate Limit Reached", "Sync requests temporarily hit Google Drive's rate limit; GhostCode will retry safely later.", "Retry", "retry_sync"),

            ErrorCategory.PermissionDenied => vi
                ? ("Không có quyền truy cập tệp hoặc thư mục", "Hệ thống từ chối quyền đọc/ghi tại đường dẫn được chọn; hãy kiểm tra quyền hoặc chọn thư mục khác.", "Chọn thư mục khác", "open_settings")
                : ("Access Permission Denied", "Windows denied read or write access at this path; please verify permissions or choose another folder.", "Choose Folder", "open_settings"),

            ErrorCategory.InvalidFilePath => vi
                ? ("Đường dẫn tệp không hợp lệ", "Đường dẫn chứa ký tự không hỗ trợ hoặc trỏ vào gốc ổ đĩa bị chặn để đảm bảo an toàn.", "Kiểm tra lại", "open_settings")
                : ("Invalid or Blocked File Path", "The selected path is malformed or targets a drive root that cannot be watched safely.", "Review Folders", "open_settings"),

            ErrorCategory.WatchedFolderUnavailable => vi
                ? ("Thư mục giám sát không tồn tại", "Một thư mục đang được theo dõi đã bị di chuyển hoặc đổi tên; hãy chọn lại thư mục nguồn.", "Mở Cài đặt", "open_settings")
                : ("Watched Folder Unavailable", "A monitored folder was moved or removed; please select an active source directory.", "Open Settings", "open_settings"),

            ErrorCategory.BrowserExtensionMissing => vi
                ? ("Tiện ích trình duyệt chưa được cài đặt", "Cài tiện ích mở rộng GhostCode vào trình duyệt để tự động nhận diện mã bài tập.", "Cài tiện ích", "open_browser_setup")
                : ("Browser Extension Not Installed", "Install the GhostCode browser extension to detect coding problems automatically.", "Install Extension", "open_browser_setup"),

            ErrorCategory.NativeMessagingFailure => vi
                ? ("Cầu nối Native Messaging chưa sẵn sàng", "Trình duyệt không thể giao tiếp trực tiếp với GhostCode; hãy nhấn Sửa kết nối để đăng ký lại cầu nối.", "Sửa kết nối", "open_browser_setup")
                : ("Native Messaging Bridge Failed", "The browser could not reach GhostCode natively; repair the bridge registration to restore pairing.", "Repair Bridge", "open_browser_setup"),

            ErrorCategory.BridgeAuthenticationFailure => vi
                ? ("Xác thực tiện ích trình duyệt thất bại", "Mã ghép nối giữa trình duyệt và ứng dụng không khớp; hãy cài đặt lại cầu nối tiện ích.", "Sửa kết nối", "open_browser_setup")
                : ("Extension Authentication Failed", "The pairing token between browser extension and app mismatched; re-stage the extension bridge.", "Repair Bridge", "open_browser_setup"),

            ErrorCategory.CompilerMissing => vi
                ? ("Không tìm thấy trình biên dịch phù hợp", "Hãy cài đặt GCC/G++/Python hoặc chỉ định đường dẫn trong trang Công cụ lập trình.", "Cấu hình công cụ", "open_devtools")
                : ("Compiler or Interpreter Not Found", "Install GCC/G++/Python or select the executable path in Development Tools.", "Configure Tools", "open_devtools"),

            ErrorCategory.CompilationError => vi
                ? ("Mã nguồn biên dịch thất bại", "Kiểm tra chi tiết thông báo lỗi cú pháp từ trình biên dịch ở khung bên dưới.", "Xem chi tiết", "open_archive")
                : ("Compilation Failed", "Check the compiler diagnostics panel below for syntax or linker errors.", "View Output", "open_archive"),

            ErrorCategory.RuntimeError => vi
                ? ("Chương trình dừng với lỗi thực thi", "Chương trình thoát với mã khác 0 hoặc gặp lỗi truy cập bộ nhớ khi đang chạy.", "Kiểm tra nhập/xuất", "open_archive")
                : ("Program Exited With Runtime Error", "The program terminated abnormally or returned a non-zero exit code.", "Inspect Output", "open_archive"),

            ErrorCategory.ExecutionTimeout => vi
                ? ("Chương trình chạy quá thời gian cho phép", "Thực thi đã tự động dừng do vượt quá giới hạn thời gian; hãy kiểm tra vòng lặp hoặc tăng giới hạn.", "Chỉnh giới hạn", "open_devtools")
                : ("Execution Timed Out", "The run was stopped because it exceeded the configured time limit.", "Adjust Limits", "open_devtools"),

            ErrorCategory.ArchiveIntegrityFailure => vi
                ? ("Lỗi kiểm tra toàn vẹn tệp lưu trữ", "Tệp sao lưu cục bộ không khớp mã băm SHA-256 gốc; bạn có thể kiểm tra và khôi phục từ bản sao khác.", "Kiểm tra khôi phục", "run_recovery")
                : ("Archive Integrity Check Failed", "A local archive file did not match its stored SHA-256 hash; run recovery verification.", "Run Recovery", "run_recovery"),

            ErrorCategory.DatabaseError => vi
                ? ("Cơ sở dữ liệu lưu trữ gặp sự cố", "Không thể đọc hoặc ghi nhật ký phiên bản vào SQLite; kiểm tra quyền ghi đĩa và chạy kiểm tra hệ thống.", "Kiểm tra hệ thống", "run_recovery")
                : ("Archive Database Error", "GhostCode could not read or write the local SQLite database cleanly.", "Check System", "run_recovery"),

            ErrorCategory.CloudMigrationConflict => vi
                ? ("Xung đột khi chuyển dữ liệu đám mây", "Một số phiên bản giữa Google Drive và OneDrive có khác biệt; dữ liệu của bạn được giữ nguyên cả hai nhánh.", "Xem hàng đợi", "open_cloud")
                : ("Cloud Migration Conflict Detected", "Some versions differed between cloud providers; all branches remain safely preserved.", "Open Drive", "open_cloud"),

            _ => vi
                ? ("Đã xảy ra lỗi không mong muốn", "Vui lòng kiểm tra chi tiết kỹ thuật bên dưới hoặc thử lại thao tác.", "Thử lại", "retry_sync")
                : ("Unexpected Error", "Check the technical details below or retry the operation.", "Retry", "retry_sync")
        };

        return new FriendlyErrorInfo(
            category,
            title,
            explanation,
            actionLabel,
            actionKey,
            RedactSecrets(rawTechnicalDetails),
            category == ErrorCategory.CloudApiDisabled ? GoogleApiErrorParser.DefaultDriveApiConsoleUrl : null);
    }

    public static FriendlyErrorInfo MapError(
        CloudServiceErrorDetails details,
        UiLanguage language)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Provider: {details.Provider} (HTTP {details.HttpStatusCode})");
        if (!string.IsNullOrWhiteSpace(details.ErrorStatus))
            sb.AppendLine($"Status: {details.ErrorStatus}");
        if (!string.IsNullOrWhiteSpace(details.Reason))
            sb.AppendLine($"Reason: {details.Reason}");
        if (!string.IsNullOrWhiteSpace(details.ServiceName))
            sb.AppendLine($"Service: {details.ServiceName}");
        if (!string.IsNullOrWhiteSpace(details.ProjectNumber))
            sb.AppendLine($"Google Cloud Project: {details.ProjectNumber}");
        if (!string.IsNullOrWhiteSpace(details.OAuthProjectNote))
            sb.AppendLine($"OAuth Check: {details.OAuthProjectNote}");
        if (!string.IsNullOrWhiteSpace(details.ShortMessage))
            sb.AppendLine($"Message: {details.ShortMessage}");
        if (!string.IsNullOrWhiteSpace(details.ValidatedActivationUrl))
            sb.AppendLine($"Activation URL: {details.ValidatedActivationUrl}");

        var mapped = MapError(details.Category, sb.ToString().TrimEnd(), language);
        return mapped with { ActionUrl = details.ValidatedActivationUrl ?? mapped.ActionUrl };
    }

    public static FriendlyErrorInfo ClassifyAndMap(
        Exception ex,
        UiLanguage language,
        ErrorCategory fallback = ErrorCategory.RuntimeError)
    {
        if (ex is CloudServiceException cex)
            return MapError(cex.Details, language);

        var msg = ex.Message ?? string.Empty;
        ErrorCategory cat = fallback;

        if (ex is TimeoutException or TaskCanceledException)
            cat = msg.Contains("exec", StringComparison.OrdinalIgnoreCase) ? ErrorCategory.ExecutionTimeout : ErrorCategory.ConnectionTimeout;
        else if (ex is UnauthorizedAccessException)
            cat = ErrorCategory.PermissionDenied;
        else if (ex is SqliteException)
            cat = ErrorCategory.DatabaseError;
        else if (ex is HttpRequestException hre)
            cat = hre.StatusCode is System.Net.HttpStatusCode sc
                ? GoogleApiErrorParser.Parse(sc, msg).Category
                : ErrorCategory.NetworkOffline;
        else if (ex is SocketException)
            cat = ErrorCategory.NetworkOffline;
        else if (msg.Contains("SERVICE_DISABLED", StringComparison.OrdinalIgnoreCase) || msg.Contains("accessNotConfigured", StringComparison.OrdinalIgnoreCase))
            cat = ErrorCategory.CloudApiDisabled;
        else if (msg.Contains("401", StringComparison.OrdinalIgnoreCase) || msg.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase) || msg.Contains("UNAUTHENTICATED", StringComparison.OrdinalIgnoreCase) || msg.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
            cat = ErrorCategory.AuthenticationExpired;
        else if (msg.Contains("quota", StringComparison.OrdinalIgnoreCase) || msg.Contains("storageQuotaExceeded", StringComparison.OrdinalIgnoreCase))
            cat = ErrorCategory.CloudQuotaExceeded;
        else if (msg.Contains("rateLimitExceeded", StringComparison.OrdinalIgnoreCase) || msg.Contains("userRateLimitExceeded", StringComparison.OrdinalIgnoreCase))
            cat = ErrorCategory.CloudRateLimited;
        else if (msg.Contains("sha", StringComparison.OrdinalIgnoreCase) || msg.Contains("corrupt", StringComparison.OrdinalIgnoreCase) || msg.Contains("integrity", StringComparison.OrdinalIgnoreCase))
            cat = ErrorCategory.ArchiveIntegrityFailure;
        else if (msg.Contains("compiler", StringComparison.OrdinalIgnoreCase) || msg.Contains("gcc", StringComparison.OrdinalIgnoreCase) || msg.Contains("g++", StringComparison.OrdinalIgnoreCase) || msg.Contains("python", StringComparison.OrdinalIgnoreCase))
            cat = ErrorCategory.CompilerMissing;
        else if (ex is DirectoryNotFoundException)
            cat = ErrorCategory.WatchedFolderUnavailable;
        else if (ex is ArgumentException or NotSupportedException or PathTooLongException)
            cat = ErrorCategory.InvalidFilePath;

        return MapError(cat, ex.ToString(), language);
    }

    public static string RedactSecrets(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var clean = BearerRegex().Replace(raw, "Bearer [REDACTED]");
        clean = KeyValueSecretRegex().Replace(clean, "$1=[REDACTED]");
        clean = JsonSecretRegex().Replace(clean, "\"$1\": \"[REDACTED]\"");
        return clean.Length > 2000 ? clean[..2000] + "..." : clean;
    }

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"(access_token|refresh_token|client_secret|pairing_secret|session_token|X-GhostCode-Token)\s*[:=]\s*[""']?([^""'\s&;]+)[""']?", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueSecretRegex();

    [GeneratedRegex("\"(access_token|refresh_token|client_secret|pairing_secret|session_token)\"\\s*:\\s*\"[^\"]+\"", RegexOptions.IgnoreCase)]
    private static partial Regex JsonSecretRegex();
}

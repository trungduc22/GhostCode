using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using GhostCode.Core.Notifications;

namespace GhostCode.Core.Cloud;

public sealed record CloudServiceErrorDetails(
    ErrorCategory Category,
    string Provider,
    int HttpStatusCode,
    string ErrorStatus,
    string Reason,
    string ServiceName,
    string ShortMessage,
    string? ProjectNumber,
    string? ValidatedActivationUrl,
    bool? OAuthProjectMatchesError,
    string OAuthProjectNote);

public sealed class CloudServiceException : InvalidOperationException
{
    public CloudServiceException(CloudServiceErrorDetails details, Exception? inner = null)
        : base(BuildConciseMessage(details), inner)
    {
        Details = details;
    }

    public CloudServiceErrorDetails Details { get; }

    private static string BuildConciseMessage(CloudServiceErrorDetails d)
    {
        var reasonTag = !string.IsNullOrWhiteSpace(d.Reason) ? d.Reason : d.ErrorStatus;
        return string.IsNullOrWhiteSpace(reasonTag)
            ? $"{d.Provider} HTTP {d.HttpStatusCode}: {d.ShortMessage}"
            : $"{d.Provider} HTTP {d.HttpStatusCode} ({reasonTag}): {d.ShortMessage}";
    }
}

/// <summary>
/// Parses structured Google API JSON error payloads (`error.status`, `error.errors[].reason`,
/// `error.details[].reason`, `error.details[].metadata.*`) into typed domain categories with
/// validated console URLs and OAuth project consistency checks.
/// </summary>
public static partial class GoogleApiErrorParser
{
    public const string DefaultDriveApiConsoleUrl = "https://console.cloud.google.com/apis/library/drive.googleapis.com";

    public static CloudServiceException CreateException(
        HttpStatusCode statusCode,
        string? responseBody,
        string? configuredOAuthClientId = null,
        string provider = "Google Drive") =>
        new(Parse(statusCode, responseBody, configuredOAuthClientId, provider));

    public static CloudServiceErrorDetails Parse(
        HttpStatusCode statusCode,
        string? responseBody,
        string? configuredOAuthClientId = null,
        string provider = "Google Drive")
    {
        int code = (int)statusCode;
        string errorStatus = string.Empty;
        string errorMessage = string.Empty;
        string firstReason = string.Empty;
        string detailReason = string.Empty;
        string serviceName = string.Empty;
        string? rawActivationUrl = null;
        string? errorProject = null;

        if (!string.IsNullOrWhiteSpace(responseBody))
        {
            var trimmed = responseBody.TrimStart();
            if (trimmed.StartsWith('{'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(trimmed);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("error", out var errObj) && errObj.ValueKind == JsonValueKind.Object)
                    {
                        errorStatus = GetString(errObj, "status");
                        errorMessage = GetString(errObj, "message");

                        if (errObj.TryGetProperty("errors", out var errorsArr) && errorsArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in errorsArr.EnumerateArray())
                            {
                                var r = GetString(item, "reason");
                                if (!string.IsNullOrWhiteSpace(r))
                                {
                                    firstReason = r;
                                    break;
                                }
                            }
                        }

                        if (errObj.TryGetProperty("details", out var detailsArr) && detailsArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var detail in detailsArr.EnumerateArray())
                            {
                                var dr = GetString(detail, "reason");
                                if (string.IsNullOrWhiteSpace(detailReason) && !string.IsNullOrWhiteSpace(dr))
                                    detailReason = dr;

                                if (detail.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
                                {
                                    var svc = GetString(meta, "service");
                                    if (!string.IsNullOrWhiteSpace(svc))
                                        serviceName = svc;

                                    var act = GetString(meta, "activationUrl");
                                    if (!string.IsNullOrWhiteSpace(act))
                                        rawActivationUrl = act;

                                    var consumer = GetString(meta, "consumer");
                                    var parsedProj = ExtractProjectNumber(consumer);
                                    if (!string.IsNullOrWhiteSpace(parsedProj))
                                        errorProject = parsedProj;
                                }
                            }
                        }
                    }
                    else if (root.TryGetProperty("error", out var strErr) && strErr.ValueKind == JsonValueKind.String)
                    {
                        firstReason = strErr.GetString() ?? string.Empty;
                        errorMessage = GetString(root, "error_description");
                    }
                }
                catch
                {
                    // Non-JSON payload handled safely below
                }
            }
        }

        if (string.IsNullOrWhiteSpace(errorProject) && !string.IsNullOrWhiteSpace(errorMessage))
            errorProject = ExtractProjectNumber(errorMessage);

        var primaryReason = !string.IsNullOrWhiteSpace(detailReason)
            ? detailReason
            : firstReason;

        var category = Classify(code, errorStatus, primaryReason, firstReason, detailReason);
        string? validatedUrl = category == ErrorCategory.CloudApiDisabled
            ? ValidateGoogleConsoleUrl(rawActivationUrl, errorProject)
            : null;

        var (projMatch, projNote) = CheckOAuthProjectConsistency(configuredOAuthClientId, errorProject);

        var concise = !string.IsNullOrWhiteSpace(errorMessage)
            ? TruncateOneLine(errorMessage, 220)
            : $"HTTP {code} {statusCode}";

        return new CloudServiceErrorDetails(
            Category: category,
            Provider: provider,
            HttpStatusCode: code,
            ErrorStatus: errorStatus,
            Reason: primaryReason,
            ServiceName: serviceName,
            ShortMessage: concise,
            ProjectNumber: errorProject,
            ValidatedActivationUrl: validatedUrl,
            OAuthProjectMatchesError: projMatch,
            OAuthProjectNote: projNote);
    }

    public static string ValidateGoogleConsoleUrl(string? rawUrl, string? projectNumber)
    {
        if (!string.IsNullOrWhiteSpace(rawUrl)
            && Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && (string.Equals(uri.Host, "console.developers.google.com", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "console.cloud.google.com", StringComparison.OrdinalIgnoreCase)))
        {
            return uri.ToString();
        }

        if (!string.IsNullOrWhiteSpace(projectNumber) && Regex.IsMatch(projectNumber, @"^\d{4,20}$"))
            return $"https://console.developers.google.com/apis/api/drive.googleapis.com/overview?project={projectNumber}";

        return DefaultDriveApiConsoleUrl;
    }

    public static string? ExtractProjectNumberFromClientId(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return null;
        var m = ClientIdProjectRegex().Match(clientId.Trim());
        return m.Success ? m.Groups[1].Value : null;
    }

    private static (bool? Matches, string Note) CheckOAuthProjectConsistency(string? clientId, string? errorProject)
    {
        var clientProj = ExtractProjectNumberFromClientId(clientId);
        if (string.IsNullOrWhiteSpace(clientProj) || string.IsNullOrWhiteSpace(errorProject))
            return (null, string.Empty);

        if (string.Equals(clientProj, errorProject, StringComparison.Ordinal))
            return (true, $"OAuth Client ID matches Google Cloud project {errorProject}.");

        return (false, $"OAuth Client ID belongs to project {clientProj}, whereas API error reported project {errorProject}. Ensure Google Drive API is enabled on the OAuth client's project.");
    }

    private static ErrorCategory Classify(
        int code,
        string errorStatus,
        string primaryReason,
        string firstReason,
        string detailReason)
    {
        bool IsReason(string target) =>
            string.Equals(primaryReason, target, StringComparison.OrdinalIgnoreCase)
            || string.Equals(firstReason, target, StringComparison.OrdinalIgnoreCase)
            || string.Equals(detailReason, target, StringComparison.OrdinalIgnoreCase);

        if (IsReason("SERVICE_DISABLED") || IsReason("accessNotConfigured"))
            return ErrorCategory.CloudApiDisabled;

        if (IsReason("storageQuotaExceeded"))
            return ErrorCategory.CloudQuotaExceeded;

        if (IsReason("insufficientPermissions"))
            return ErrorCategory.CloudInsufficientPermissions;

        if (IsReason("rateLimitExceeded") || IsReason("userRateLimitExceeded") || code == 429)
            return ErrorCategory.CloudRateLimited;

        if (code == 401
            || string.Equals(errorStatus, "UNAUTHENTICATED", StringComparison.OrdinalIgnoreCase)
            || IsReason("invalid_grant")
            || IsReason("authError"))
            return ErrorCategory.AuthenticationExpired;

        if (code == 403 || string.Equals(errorStatus, "PERMISSION_DENIED", StringComparison.OrdinalIgnoreCase))
            return ErrorCategory.PermissionDenied;

        if (code == 408 || code == 504)
            return ErrorCategory.ConnectionTimeout;

        return ErrorCategory.PermissionDenied;
    }

    private static string? ExtractProjectNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var m = ProjectNumRegex().Match(text);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string GetString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? (el.GetString() ?? string.Empty)
            : string.Empty;

    private static string TruncateOneLine(string text, int maxLen)
    {
        var single = Regex.Replace(text, @"\s+", " ").Trim();
        return single.Length <= maxLen ? single : single[..maxLen] + "...";
    }

    [GeneratedRegex(@"^(\d{4,20})-[A-Za-z0-9_.-]+\.apps\.googleusercontent\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex ClientIdProjectRegex();

    [GeneratedRegex(@"(?:projects/|project[=\s]+)(\d{4,20})", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectNumRegex();
}

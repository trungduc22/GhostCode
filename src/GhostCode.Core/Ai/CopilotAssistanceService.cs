using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GhostCode.Core.Models;

namespace GhostCode.Core.Ai;

public sealed record AiSuggestionResponse(
    bool Succeeded,
    string Content,
    string Disclaimer,
    string StatusNote);

/// <summary>
/// Strictly opt-in AI/Copilot assistance wrapper that honors GitHub Copilot API/licensing boundaries.
/// Disabled by default (AiAssistanceOptIn = false), never scrapes VS Code or browser tokens,
/// never transmits code automatically, and requires explicit user-provided API credentials.
/// </summary>
public sealed class CopilotAssistanceService
{
    public const string RequiredDisclaimer = "[AI-generated suggestion — verify before using]";

    private readonly GhostCodeConfig _config;
    private readonly HttpClient _http;
    private string? _ephemeralUserApiToken;
    private string _endpointUrl = "https://models.inference.ai.azure.com/chat/completions";

    public CopilotAssistanceService(GhostCodeConfig config, HttpClient? httpClient = null)
    {
        _config = config;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public void ConfigureUserProvidedToken(string? personalAccessToken, string? endpointOverride = null)
    {
        _ephemeralUserApiToken = string.IsNullOrWhiteSpace(personalAccessToken) ? null : personalAccessToken.Trim();
        if (!string.IsNullOrWhiteSpace(endpointOverride))
            _endpointUrl = endpointOverride.Trim();
    }

    public string GetAvailabilityStatus()
    {
        if (!_config.AiAssistanceOptIn)
        {
            return "Disabled (Opt-In required in Settings). GhostCode never scrapes VS Code Copilot tokens; connecting requires an explicit user-authorized GitHub Models / Copilot API token.";
        }

        if (string.IsNullOrWhiteSpace(_ephemeralUserApiToken))
        {
            return "Opt-in enabled, waiting for explicit user GitHub Models / Copilot API token (Copilot consumer extensions are restricted to IDE hosts like VS Code / Visual Studio).";
        }

        return "Opt-in AI assistant ready (explicit user action only; suggestions are labeled before display).";
    }

    public async Task<AiSuggestionResponse> RequestExplanationOrHintAsync(
        string codeSnippet,
        string prompt,
        CancellationToken ct = default)
    {
        if (!_config.AiAssistanceOptIn)
        {
            return new AiSuggestionResponse(
                false,
                string.Empty,
                RequiredDisclaimer,
                "Blocked: AI Assistance Opt-In is disabled. Enable it explicitly in Settings to send code for hints.");
        }

        if (string.IsNullOrWhiteSpace(_ephemeralUserApiToken))
        {
            return new AiSuggestionResponse(
                false,
                string.Empty,
                RequiredDisclaimer,
                "Blocked: No user-provided GitHub Models / Copilot API token configured. GhostCode does not extract VS Code credentials.");
        }

        var payload = new
        {
            model = "gpt-4o-mini",
            messages = new object[]
            {
                new { role = "system", content = "Provide concise debugging or algorithmic hints without writing full cheated contest submissions." },
                new { role = "user", content = $"{prompt}\n\nCode:\n{codeSnippet}" }
            },
            temperature = 0.2,
            max_tokens = 450
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, _endpointUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _ephemeralUserApiToken);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            return new AiSuggestionResponse(false, string.Empty, RequiredDisclaimer, $"API request failed ({(int)resp.StatusCode}).");
        }

        string text = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var contentProp))
                    text = contentProp.GetString() ?? body;
            }
        }
        catch { }

        return new AiSuggestionResponse(
            true,
            $"{RequiredDisclaimer}\n{text.Trim()}",
            RequiredDisclaimer,
            "Completed via explicit user request");
    }
}

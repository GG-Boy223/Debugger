using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DogeDebugger.Core.Settings;

namespace DogeDebugger.Core.AI;

/// <summary>
/// Minimal OpenAI-compatible chat client used by non-conversational
/// generators such as the Lua AI preview panel.
/// </summary>
public sealed class AiChatClient
{
    private readonly AppSettings _settings;
    private readonly HttpClient _httpClient = new();

    public AiChatClient(AppSettings settings)
    {
        _settings = settings;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.AiApiUrl);

    public async Task<IReadOnlyList<string>> LoadModelsAsync(
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "/v1/models");
        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        string body = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        List<string> models = [];
        using JsonDocument document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("data", out JsonElement data) &&
            data.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement model in data.EnumerateArray())
            {
                if (model.TryGetProperty("id", out JsonElement id) &&
                    id.GetString() is { Length: > 0 } modelId)
                {
                    models.Add(modelId);
                }
            }
        }

        return models;
    }

    public async Task<string> SendChatAsync(
        string prompt,
        string? systemPrompt,
        string? model,
        CancellationToken cancellationToken = default)
    {
        List<object> messages = [];
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new { role = "system", content = systemPrompt });
        }

        messages.Add(new { role = "user", content = prompt });
        object payload = new
        {
            model = string.IsNullOrWhiteSpace(model)
                ? _settings.AiDefaultModel
                : model,
            temperature = _settings.AiTemperature,
            messages
        };
        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Post,
            "/v1/chat/completions");
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        string body = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return ExtractAssistantContent(body);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        HttpRequestMessage request = new(
            method,
            _settings.AiApiUrl.TrimEnd('/') + path);
        if (!string.IsNullOrWhiteSpace(_settings.AiApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                _settings.AiApiKey);
        }

        return request;
    }

    private static string ExtractAssistantContent(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("choices", out JsonElement choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        JsonElement message = choices[0].GetProperty("message");
        return message.TryGetProperty("content", out JsonElement content)
            ? content.GetString() ?? string.Empty
            : string.Empty;
    }
}

using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DogeDebugger.Core.Settings;

namespace DogeDebugger.UI.ViewModels.Dialogs;

public partial class AiAssistantViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly HttpClient _httpClient = new();
    private CancellationTokenSource? _requestCancellation;

    [ObservableProperty]
    private string _prompt = string.Empty;

    [ObservableProperty]
    private string _statusText = "正在获取模型列表...";

    [ObservableProperty]
    private string? _selectedModel;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _errorText = string.Empty;

    public AiAssistantViewModel(AppSettings settings)
    {
        _settings = settings;
        SelectedModel = string.IsNullOrWhiteSpace(settings.AiDefaultModel)
            ? null
            : settings.AiDefaultModel;
    }

    public ObservableCollection<string> Models { get; } = [];

    public ObservableCollection<AiChatMessage> Messages { get; } = [];

    public bool IsEmpty => Messages.Count == 0;

    [RelayCommand]
    private async Task LoadModelsAsync()
    {
        HasError = false;
        IsBusy = true;
        StatusText = "正在获取模型列表...";
        try
        {
            using HttpRequestMessage request = CreateRequest(
                HttpMethod.Get,
                "/v1/models");
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request)
                .ConfigureAwait(true);
            string body = await response.Content
                .ReadAsStringAsync()
                .ConfigureAwait(true);
            response.EnsureSuccessStatusCode();

            using JsonDocument document = JsonDocument.Parse(body);
            Models.Clear();
            if (document.RootElement.TryGetProperty("data", out JsonElement data) &&
                data.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement model in data.EnumerateArray())
                {
                    if (model.TryGetProperty("id", out JsonElement id))
                    {
                        string? modelId = id.GetString();
                        if (!string.IsNullOrWhiteSpace(modelId))
                        {
                            Models.Add(modelId);
                        }
                    }
                }
            }

            if (SelectedModel is null || !Models.Contains(SelectedModel))
            {
                SelectedModel = Models.FirstOrDefault();
            }

            StatusText = Models.Count == 0
                ? "API 没有返回可用模型。"
                : $"已加载 {Models.Count:N0} 个模型。";
        }
        catch (Exception exception)
        {
            HasError = true;
            ErrorText =
                "无法获取模型列表，请检查 AI 配置参数\r\n" +
                exception.Message;
            StatusText = "模型列表加载失败。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        string prompt = Prompt.Trim();
        if (prompt.Length == 0 || string.IsNullOrWhiteSpace(SelectedModel))
        {
            return;
        }

        Prompt = string.Empty;
        Messages.Add(new AiChatMessage("user", prompt));
        OnPropertyChanged(nameof(IsEmpty));
        AiChatMessage assistant = new("assistant", string.Empty);
        Messages.Add(assistant);
        IsBusy = true;
        StatusText = "正在等待模型响应...";
        _requestCancellation = new CancellationTokenSource();

        try
        {
            object payload = new
            {
                model = SelectedModel,
                temperature = _settings.AiTemperature,
                messages = BuildMessages(prompt)
            };
            string requestJson = JsonSerializer.Serialize(payload);
            using HttpRequestMessage request = CreateRequest(
                HttpMethod.Post,
                "/v1/chat/completions");
            request.Content = new StringContent(
                requestJson,
                Encoding.UTF8,
                "application/json");
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, _requestCancellation.Token)
                .ConfigureAwait(true);
            string body = await response.Content
                .ReadAsStringAsync(_requestCancellation.Token)
                .ConfigureAwait(true);
            response.EnsureSuccessStatusCode();
            assistant.Content = ExtractAssistantContent(body);
            StatusText = "响应完成。";
        }
        catch (OperationCanceledException)
        {
            assistant.Content = "已停止。";
            StatusText = "响应已停止。";
        }
        catch (Exception exception)
        {
            assistant.Content = exception.Message;
            HasError = true;
            ErrorText = exception.Message;
            StatusText = "请求失败。";
        }
        finally
        {
            IsBusy = false;
            _requestCancellation.Dispose();
            _requestCancellation = null;
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _requestCancellation?.Cancel();
    }

    [RelayCommand]
    private void NewConversation()
    {
        Messages.Clear();
        Prompt = string.Empty;
        HasError = false;
        ErrorText = string.Empty;
        StatusText = Models.Count == 0
            ? "正在获取模型列表..."
            : $"已加载 {Models.Count:N0} 个模型。";
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnPromptChanged(string value)
    {
        SendCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedModelChanged(string? value)
    {
        SendCommand.NotifyCanExecuteChanged();
    }

    private bool CanSend()
    {
        return !IsBusy &&
               !string.IsNullOrWhiteSpace(Prompt) &&
               !string.IsNullOrWhiteSpace(SelectedModel);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        string baseUrl = _settings.AiApiUrl.TrimEnd('/');
        HttpRequestMessage request = new(method, baseUrl + path);
        if (!string.IsNullOrWhiteSpace(_settings.AiApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                _settings.AiApiKey);
        }

        return request;
    }

    private IEnumerable<object> BuildMessages(string prompt)
    {
        if (!string.IsNullOrWhiteSpace(_settings.AiSystemPrompt))
        {
            yield return new { role = "system", content = _settings.AiSystemPrompt };
        }

        foreach (AiChatMessage message in Messages
                     .Where(static message => message.Role != "assistant" ||
                                              message.Content.Length > 0))
        {
            if (ReferenceEquals(message, Messages.LastOrDefault()))
            {
                continue;
            }

            yield return new { role = message.Role, content = message.Content };
        }

        yield return new { role = "user", content = prompt };
    }

    private static string ExtractAssistantContent(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty(
                "choices",
                out JsonElement choices) ||
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

public partial class AiChatMessage : ObservableObject
{
    [ObservableProperty]
    private string _content;

    public AiChatMessage(string role, string content)
    {
        Role = role;
        _content = content;
    }

    public string Role { get; }

    public bool IsUser => Role == "user";

    public bool IsAssistant => !IsUser;
}

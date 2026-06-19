using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Re0Agent.Core.Services.Llm;

public sealed class OpenAiCompatibleLlmClient(HttpClient httpClient) : ILlmClient
{
    public async Task<LlmResponse> SendChatAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Options is null || !request.Options.HasUsableEndpoint)
        {
            throw new InvalidOperationException("LLM request requires a usable endpoint configuration.");
        }

        using var httpRequest = CreateHttpRequest(request, stream: false);
        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return new LlmResponse(request.AgentName, string.Empty, body);
        }

        var (content, reasoning) = ReadAssistantContent(body);
        return new LlmResponse(request.AgentName, content, ReasoningContent: reasoning);
    }

    public async IAsyncEnumerable<LlmStreamChunk> StreamChatAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (request.Options is null || !request.Options.HasUsableEndpoint)
        {
            throw new InvalidOperationException("LLM request requires a usable endpoint configuration.");
        }

        using var httpRequest = CreateHttpRequest(request, stream: true);
        using var response = await httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        await foreach (var delta in SseParser.ReadContentDeltasAsync(reader, cancellationToken))
        {
            yield return new LlmStreamChunk(request.AgentName, delta, IsDone: false);
        }

        yield return new LlmStreamChunk(request.AgentName, string.Empty, IsDone: true);
    }

    private static HttpRequestMessage CreateHttpRequest(LlmRequest request, bool stream)
    {
        var options = request.Options!;
        var endpoint = NormalizeChatCompletionsEndpoint(options.ApiEndpoint);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.ModelName,
            ["messages"] = request.Messages.Select(message => new { role = message.Role, content = message.Content }),
            ["temperature"] = options.Temperature,
            ["max_tokens"] = options.MaxTokens,
            ["stream"] = stream
        };

        if (options.ResponseFormat == "JSON")
        {
            payload["response_format"] = new { type = "json_object" };
        }

        // DeepSeek 思考模式（OpenAI 格式）：thinking.type 是真正的开关，
        // 且 DeepSeek 默认 enabled，故关闭时也必须显式发送 disabled。
        if (IsDeepSeek(options))
        {
            payload["thinking"] = new { type = options.EnableThinking ? "enabled" : "disabled" };
            if (options.EnableThinking)
            {
                payload["reasoning_effort"] = string.IsNullOrWhiteSpace(options.ReasoningEffort)
                    ? "medium"
                    : options.ReasoningEffort;
            }
        }

        httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return httpRequest;
    }

    private static bool IsDeepSeek(LlmOptions options) =>
        (options.ApiEndpoint?.Contains("deepseek", StringComparison.OrdinalIgnoreCase) ?? false)
        || (options.ModelName?.Contains("deepseek", StringComparison.OrdinalIgnoreCase) ?? false);

    private static string NormalizeChatCompletionsEndpoint(string endpoint)
    {
        var trimmed = endpoint.Trim();
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"{trimmed.TrimEnd('/')}/chat/completions";
    }

    private static (string Content, string? Reasoning) ReadAssistantContent(string body)
    {
        using var document = JsonDocument.Parse(body);
        var choices = document.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
        {
            return (string.Empty, null);
        }

        var message = choices[0].GetProperty("message");
        var content = message.TryGetProperty("content", out var c) ? c.GetString() ?? string.Empty : string.Empty;

        // 推理模型（DeepSeek-R1 等）将思维链放在 reasoning_content / reasoning 字段。
        string? reasoning = null;
        if (message.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
        {
            reasoning = rc.GetString();
        }
        else if (message.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.String)
        {
            reasoning = r.GetString();
        }

        // 部分模型把思维链以 <think>...</think> 内联在 content 里，剥离出来单独展示。
        if (string.IsNullOrEmpty(reasoning))
        {
            var (stripped, thought) = ExtractInlineThink(content);
            if (thought is not null)
            {
                content = stripped;
                reasoning = thought;
            }
        }

        return (content, string.IsNullOrWhiteSpace(reasoning) ? null : reasoning);
    }

    private static (string Content, string? Thought) ExtractInlineThink(string content)
    {
        if (string.IsNullOrEmpty(content)) return (content, null);

        var match = System.Text.RegularExpressions.Regex.Match(
            content,
            @"<think>(.*?)</think>",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        if (!match.Success) return (content, null);

        var thought = match.Groups[1].Value.Trim();
        var stripped = content.Remove(match.Index, match.Length).Trim();
        return (stripped, thought);
    }
}

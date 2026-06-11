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

        return new LlmResponse(request.AgentName, ReadAssistantContent(body));
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

        object payload;
        if (options.ResponseFormat == "JSON")
        {
            payload = new
            {
                model = options.ModelName,
                messages = request.Messages.Select(message => new { role = message.Role, content = message.Content }),
                temperature = options.Temperature,
                max_tokens = options.MaxTokens,
                stream,
                response_format = new { type = "json_object" }
            };
        }
        else
        {
            payload = new
            {
                model = options.ModelName,
                messages = request.Messages.Select(message => new { role = message.Role, content = message.Content }),
                temperature = options.Temperature,
                max_tokens = options.MaxTokens,
                stream
            };
        }

        httpRequest.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return httpRequest;
    }

    private static string NormalizeChatCompletionsEndpoint(string endpoint)
    {
        var trimmed = endpoint.Trim();
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"{trimmed.TrimEnd('/')}/chat/completions";
    }

    private static string ReadAssistantContent(string body)
    {
        using var document = JsonDocument.Parse(body);
        var choices = document.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var message = choices[0].GetProperty("message");
        return message.TryGetProperty("content", out var content) ? content.GetString() ?? string.Empty : string.Empty;
    }
}

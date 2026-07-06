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

        // 流式开关开启时：走 SSE 流式接口拉流并在本地聚合成完整文本，
        // 再复用 ExtractInlineThink 剥离思维链，使上层管线保持不变。
        if (Re0Agent.Core.Services.Agent.GameProgressService.StreamingGlobal)
        {
            return await SendChatViaStreamAsync(request, cancellationToken);
        }

        using var httpRequest = CreateHttpRequest(request, stream: false);
        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateHttpErrorException(response, body);
        }

        var (content, reasoning) = ReadAssistantContent(body);
        var usage = ParseUsageFromBody(body);
        return new LlmResponse(request.AgentName, content, ReasoningContent: reasoning, Usage: usage);
    }

    private async Task<LlmResponse> SendChatViaStreamAsync(
        LlmRequest request,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        LlmUsage? usage = null;
        await foreach (var chunk in StreamChatAsync(request, cancellationToken))
        {
            if (chunk.Usage is not null)
            {
                usage = chunk.Usage;
            }
            if (!chunk.IsDone && !string.IsNullOrEmpty(chunk.ContentDelta))
            {
                builder.Append(chunk.ContentDelta);
            }
        }

        var raw = builder.ToString();
        var (content, thought) = ExtractInlineThink(raw);
        return new LlmResponse(
            request.AgentName,
            content,
            ReasoningContent: string.IsNullOrWhiteSpace(thought) ? null : thought,
            Usage: usage);
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

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw CreateHttpErrorException(response, body);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        LlmUsage? usage = null;
        await foreach (var item in SseParser.ReadStreamAsync(reader, cancellationToken))
        {
            if (item.Usage is not null)
            {
                usage = item.Usage;
            }
            else if (item.Content is not null)
            {
                yield return new LlmStreamChunk(request.AgentName, item.Content, IsDone: false);
            }
        }

        yield return new LlmStreamChunk(request.AgentName, string.Empty, IsDone: true, Usage: usage);
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
            ["messages"] = BuildMessages(request.Messages, options),
            ["temperature"] = options.Temperature,
            ["max_tokens"] = options.MaxTokens,
            ["stream"] = stream
        };

        // 流式请求要求服务端在末尾追加 usage 块（DeepSeek/OpenAI 支持），
        // 否则流式下拿不到缓存命中统计。
        if (stream && SupportsStreamUsageOptions(options))
        {
            payload["stream_options"] = new { include_usage = true };
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

    private static HttpRequestException CreateHttpErrorException(
        HttpResponseMessage response,
        string responseBody)
    {
        var body = NormalizeErrorBody(responseBody);
        var message = $"LLM 请求失败 (HTTP {(int)response.StatusCode} {response.StatusCode})";
        if (!string.IsNullOrWhiteSpace(body))
        {
            message += $": {body}";
        }

        return new HttpRequestException(message, inner: null, response.StatusCode);
    }

    private static string NormalizeErrorBody(string responseBody)
    {
        var body = responseBody.Trim();
        const int maxLength = 4_000;
        return body.Length <= maxLength ? body : body[..maxLength] + "...";
    }

    private static bool IsDeepSeek(LlmOptions options) =>
        (options.ApiEndpoint?.Contains("deepseek", StringComparison.OrdinalIgnoreCase) ?? false)
        || (options.ModelName?.Contains("deepseek", StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool SupportsStreamUsageOptions(LlmOptions options)
    {
        // Google Gemini 的 OpenAI-compatible endpoint 可流式输出，但不稳定接受
        // OpenAI 的 stream_options 扩展参数；省略后仍能正常读取内容增量。
        if (options.ApiEndpoint?.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) == true)
        {
            return false;
        }

        return true;
    }

    private static bool IsClaude(LlmOptions options) =>
        (options.ModelName?.Contains("claude", StringComparison.OrdinalIgnoreCase) ?? false)
        || (options.ModelName?.Contains("anthropic", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// 构建 messages 数组并处理提示词缓存：
    /// - DeepSeek / OpenAI(GPT)：缓存是「全自动」的，无需任何请求参数——只要稳定前缀
    ///   （system 提示词 + 各 Compose 里的规则/世界书部分）逐字节不变即自动命中。因此这里
    ///   保持纯字符串 content 原样下发即可。
    /// - Claude（经 OpenAI 兼容网关，如 OpenRouter）：缓存必须「显式」声明——在稳定的
    ///   system 消息内容块上挂 cache_control: ephemeral 断点，否则一律不缓存。故仅对 Claude
    ///   把 system 消息转成带 cache_control 的结构化内容块。
    /// </summary>
    private static IEnumerable<object> BuildMessages(IReadOnlyList<LlmMessage> messages, LlmOptions options)
    {
        bool claude = IsClaude(options);
        bool systemBreakpointPlaced = false;

        foreach (var message in messages)
        {
            var content = PromptMacros.Expand(message.Content);

            // 仅对 Claude 的首个 system 消息挂缓存断点：system 渲染在 messages 之前，
            // 是最大的稳定前缀（人设/世界书/填表规则），断点放这里能把它整段缓存。
            if (claude && message.Role == "system" && !systemBreakpointPlaced && !string.IsNullOrEmpty(content))
            {
                systemBreakpointPlaced = true;
                yield return new
                {
                    role = message.Role,
                    content = new object[]
                    {
                        new
                        {
                            type = "text",
                            text = content,
                            cache_control = new { type = "ephemeral" }
                        }
                    }
                };
                continue;
            }

            yield return new { role = message.Role, content };
        }
    }

    /// <summary>解析响应里的 token 用量，归一化各厂商不同的缓存命中字段名。</summary>
    private static LlmUsage? ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int prompt = ReadInt(usage, "prompt_tokens");
        int completion = ReadInt(usage, "completion_tokens");

        // 缓存命中（读）：DeepSeek=prompt_cache_hit_tokens；
        // OpenAI/GPT=prompt_tokens_details.cached_tokens；Claude=cache_read_input_tokens。
        int cached = ReadInt(usage, "prompt_cache_hit_tokens")
            + ReadInt(usage, "cache_read_input_tokens");
        if (cached == 0
            && usage.TryGetProperty("prompt_tokens_details", out var details)
            && details.ValueKind == JsonValueKind.Object)
        {
            cached += ReadInt(details, "cached_tokens");
        }

        // 缓存写入（仅 Claude 计费区分）：cache_creation_input_tokens。
        int cacheWrite = ReadInt(usage, "cache_creation_input_tokens");

        return new LlmUsage(prompt, completion, cached, cacheWrite);
    }

    private static int ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static LlmUsage? ParseUsageFromBody(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return ParseUsage(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
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

        // 部分模型把思维链以 <think>/<thought> 内联在 content 里，剥离出来单独展示，
        // 避免思维链进入后续轮次传给 AI 的上下文。
        var (stripped, thought) = ExtractInlineThink(content);
        if (thought is not null)
        {
            content = stripped;
            if (string.IsNullOrEmpty(reasoning))
            {
                reasoning = thought;
            }
        }

        return (content, string.IsNullOrWhiteSpace(reasoning) ? null : reasoning);
    }

    private static (string Content, string? Thought) ExtractInlineThink(string content)
    {
        if (string.IsNullOrEmpty(content)) return (content, null);

        // 同时剥离 <think>…</think> 与 <thought>…</thought>（可多段），大小写不敏感。
        var matches = System.Text.RegularExpressions.Regex.Matches(
            content,
            @"<(think|thought)>(.*?)</\1>",
            System.Text.RegularExpressions.RegexOptions.Singleline
                | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (matches.Count == 0)
        {
            // 场景一：assistant 前缀填充 —— <thought> 开标签在 prefill 里（不回显），
            // 响应仅含「思维链续写 + </thought>」。剥离首个闭标签及其之前的全部内容。
            var loneClose = System.Text.RegularExpressions.Regex.Match(
                content,
                @"</(think|thought)>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (loneClose.Success
                && !content[..loneClose.Index].Contains('<'))
            {
                var thoughtHead = content[..loneClose.Index].Trim();
                var bodyTail = content[(loneClose.Index + loneClose.Length)..].Trim();
                return (bodyTail, thoughtHead);
            }

            // 场景二：开标签存在但未闭合（如输出被截断），剥离从该标签起的剩余内容。
            var openMatch = System.Text.RegularExpressions.Regex.Match(
                content,
                @"<(think|thought)>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (openMatch.Success)
            {
                var head = content[..openMatch.Index].Trim();
                var tail = content[(openMatch.Index + openMatch.Length)..].Trim();
                return (head, tail);
            }

            return (content, null);
        }

        var thoughts = new System.Text.StringBuilder();
        var stripped = content;
        // 从后往前移除，保持索引有效。
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var match = matches[i];
            thoughts.Insert(0, match.Groups[2].Value.Trim() + "\n");
            stripped = stripped.Remove(match.Index, match.Length);
        }

        return (stripped.Trim(), thoughts.ToString().Trim());
    }
}

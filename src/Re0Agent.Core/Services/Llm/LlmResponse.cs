namespace Re0Agent.Core.Services.Llm;

public sealed record LlmResponse(
    string AgentName,
    string Content,
    string? ErrorMessage = null,
    string? ReasoningContent = null,
    LlmUsage? Usage = null);

/// <summary>
/// 一次请求的 token 用量，重点是缓存命中统计。
/// 字段在不同厂商响应里名字不同，已在客户端归一化：
/// DeepSeek 用 prompt_cache_hit_tokens / prompt_cache_miss_tokens；
/// OpenAI/GPT 用 prompt_tokens_details.cached_tokens；
/// Claude(OpenAI 兼容网关) 用 cache_read_input_tokens / cache_creation_input_tokens。
/// </summary>
public sealed record LlmUsage(
    int PromptTokens = 0,
    int CompletionTokens = 0,
    int CachedTokens = 0,
    int CacheWriteTokens = 0);

public sealed record LlmStreamChunk(
    string AgentName,
    string ContentDelta,
    bool IsDone,
    LlmUsage? Usage = null);

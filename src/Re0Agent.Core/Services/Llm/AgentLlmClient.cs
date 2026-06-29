using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Re0Agent.Core.Services.Llm;

public sealed class AgentLlmClient(
    OpenAiCompatibleLlmClient realClient,
    LlmLogService logService) : ILlmClient
{
    private const int MaxRetries = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    public async Task<LlmResponse> SendChatAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Options is null || !request.Options.HasUsableEndpoint)
        {
            var err = $"未配置 Agent“{request.AgentName}”的有效契约 (API Key / Endpoint)。请前往“契约之书”配置您的 API 密钥与端点！";
            logService.Log(new LlmLogEntry
            {
                AgentName = request.AgentName,
                SystemPrompt = GetSystemPrompt(request),
                UserPrompt = GetUserPrompt(request),
                ErrorMessage = err
            });
            throw new InvalidOperationException(err);
        }

        var autoRetry = request.Options.AutoRetry && Re0Agent.Core.Services.Agent.GameProgressService.AutoRetryGlobal;
        var lastResponse = default(LlmResponse);
        var lastException = default(Exception);

        for (var attempt = 1; attempt <= (autoRetry ? MaxRetries : 1); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempt > 1)
            {
                await Task.Delay(RetryDelay, cancellationToken);
                logService.Log(new LlmLogEntry
                {
                    AgentName = request.AgentName,
                    SystemPrompt = GetSystemPrompt(request),
                    UserPrompt = GetUserPrompt(request),
                    ErrorMessage = $"⏳ 自动重试第 {attempt}/{MaxRetries} 次…"
                });
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var res = await realClient.SendChatAsync(request, cancellationToken);
                sw.Stop();

                // 判断输出是否有效（非空且非纯空白）
                if (!string.IsNullOrWhiteSpace(res.Content))
                {
                    logService.Log(new LlmLogEntry
                    {
                        AgentName = request.AgentName,
                        SystemPrompt = GetSystemPrompt(request),
                        UserPrompt = GetUserPrompt(request),
                        ResponseContent = res.Content,
                        ReasoningContent = res.ReasoningContent ?? string.Empty,
                        LatencyMs = sw.ElapsedMilliseconds,
                        CachedTokens = res.Usage?.CachedTokens ?? 0,
                        PromptTokens = res.Usage?.PromptTokens ?? 0
                    });
                    return res;
                }

                // 输出为空 → 触发重试
                lastResponse = res;
                sw.Stop();
                logService.Log(new LlmLogEntry
                {
                    AgentName = request.AgentName,
                    SystemPrompt = GetSystemPrompt(request),
                    UserPrompt = GetUserPrompt(request),
                    ResponseContent = res.Content,
                    ReasoningContent = res.ReasoningContent ?? string.Empty,
                    ErrorMessage = $"⚠️ 输出为空，触发自动重试 ({attempt}/{MaxRetries})",
                    LatencyMs = sw.ElapsedMilliseconds
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sw.Stop();
                lastException = ex;
                logService.Log(new LlmLogEntry
                {
                    AgentName = request.AgentName,
                    SystemPrompt = GetSystemPrompt(request),
                    UserPrompt = GetUserPrompt(request),
                    ErrorMessage = $"❌ 请求异常，触发自动重试 ({attempt}/{MaxRetries}): {ex.Message}",
                    LatencyMs = sw.ElapsedMilliseconds
                });

                if (attempt >= (autoRetry ? MaxRetries : 1))
                {
                    throw;
                }
            }
        }

        // 所有重试用完：返回最后一次空响应（如有），否则抛异常
        if (lastResponse is not null)
        {
            return lastResponse;
        }

        throw lastException ?? new InvalidOperationException("LLM 请求失败：所有重试均已耗尽。");
    }

    public IAsyncEnumerable<LlmStreamChunk> StreamChatAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Options is null || !request.Options.HasUsableEndpoint)
        {
            throw new InvalidOperationException($"未配置 Agent“{request.AgentName}”的有效契约 (API Key / Endpoint)。请前往“契约之书”配置您的 API 密钥与端点！");
        }
        return realClient.StreamChatAsync(request, cancellationToken);
    }

    private static string GetSystemPrompt(LlmRequest request)
    {
        return string.Join("\n---\n", request.Messages.Where(m => m.Role == "system").Select(m => PromptMacros.Expand(m.Content)));
    }

    private static string GetUserPrompt(LlmRequest request)
    {
        return string.Join("\n---\n", request.Messages.Where(m => m.Role != "system").Select(m => PromptMacros.Expand(m.Content)));
    }
}

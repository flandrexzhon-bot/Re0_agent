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

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var res = await realClient.SendChatAsync(request, cancellationToken);
            sw.Stop();
            logService.Log(new LlmLogEntry
            {
                AgentName = request.AgentName,
                SystemPrompt = GetSystemPrompt(request),
                UserPrompt = GetUserPrompt(request),
                ResponseContent = res.Content,
                ReasoningContent = res.ReasoningContent ?? string.Empty,
                LatencyMs = sw.ElapsedMilliseconds
            });
            return res;
        }
        catch (Exception ex)
        {
            sw.Stop();
            logService.Log(new LlmLogEntry
            {
                AgentName = request.AgentName,
                SystemPrompt = GetSystemPrompt(request),
                UserPrompt = GetUserPrompt(request),
                ErrorMessage = ex.Message,
                LatencyMs = sw.ElapsedMilliseconds
            });
            throw;
        }
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
        return string.Join("\n---\n", request.Messages.Where(m => m.Role == "system").Select(m => m.Content));
    }

    private static string GetUserPrompt(LlmRequest request)
    {
        return string.Join("\n---\n", request.Messages.Where(m => m.Role != "system").Select(m => m.Content));
    }
}

using System.Runtime.CompilerServices;

namespace Re0Agent.Core.Services.Llm;

public sealed class AgentLlmClient(
    OpenAiCompatibleLlmClient realClient) : ILlmClient
{
    public Task<LlmResponse> SendChatAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Options is null || !request.Options.HasUsableEndpoint)
        {
            throw new InvalidOperationException($"未配置 Agent“{request.AgentName}”的有效契约 (API Key / Endpoint)。请前往“契约之书”配置您的 API 密钥与端点！");
        }
        return realClient.SendChatAsync(request, cancellationToken);
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
}

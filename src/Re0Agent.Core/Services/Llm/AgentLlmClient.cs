using System.Runtime.CompilerServices;

namespace Re0Agent.Core.Services.Llm;

public sealed class AgentLlmClient(
    OpenAiCompatibleLlmClient realClient,
    FakeLlmClient fakeClient) : ILlmClient
{
    public Task<LlmResponse> SendChatAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        return request.Options is not null && request.Options.HasUsableEndpoint
            ? realClient.SendChatAsync(request, cancellationToken)
            : fakeClient.SendChatAsync(request, cancellationToken);
    }

    public IAsyncEnumerable<LlmStreamChunk> StreamChatAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        return request.Options is not null && request.Options.HasUsableEndpoint
            ? realClient.StreamChatAsync(request, cancellationToken)
            : fakeClient.StreamChatAsync(request, cancellationToken);
    }
}

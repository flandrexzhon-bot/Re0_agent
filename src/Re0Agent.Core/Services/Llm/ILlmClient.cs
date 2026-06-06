namespace Re0Agent.Core.Services.Llm;

public interface ILlmClient
{
    Task<LlmResponse> SendChatAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<LlmStreamChunk> StreamChatAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default);
}

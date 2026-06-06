namespace Re0Agent.Core.Services.Llm;

public sealed class LlmRequest
{
    public required string AgentName { get; init; }
    public LlmOptions? Options { get; init; }
    public bool Stream { get; init; }
    public IReadOnlyList<LlmMessage> Messages { get; init; } = Array.Empty<LlmMessage>();
}

namespace Re0Agent.Core.Services.Llm;

public sealed record LlmResponse(
    string AgentName,
    string Content,
    string? ErrorMessage = null,
    string? ReasoningContent = null);

public sealed record LlmStreamChunk(
    string AgentName,
    string ContentDelta,
    bool IsDone);

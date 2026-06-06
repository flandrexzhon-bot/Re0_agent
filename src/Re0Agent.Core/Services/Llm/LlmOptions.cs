namespace Re0Agent.Core.Services.Llm;

public sealed record LlmOptions(
    string ApiEndpoint,
    string ApiKey,
    string ModelName,
    double Temperature,
    int MaxTokens)
{
    public bool HasUsableEndpoint =>
        !string.IsNullOrWhiteSpace(ApiEndpoint)
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(ModelName);
}

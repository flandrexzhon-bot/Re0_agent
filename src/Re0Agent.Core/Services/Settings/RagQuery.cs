namespace Re0Agent.Core.Services.Settings;

public sealed class RagQuery
{
    public required string Text { get; init; }
    public int Chapter { get; init; } = 1;
    public int MaxNonConstantEntries { get; init; } = 8;
    public int MaxCharacters { get; init; } = 14_000;
}

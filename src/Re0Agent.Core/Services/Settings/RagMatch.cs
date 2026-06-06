namespace Re0Agent.Core.Services.Settings;

public sealed class RagMatch
{
    public required WorldBookEntry Entry { get; init; }
    public int Score { get; init; }
    public IReadOnlyList<string> MatchedKeys { get; init; } = [];
    public required string RenderedContent { get; init; }
}

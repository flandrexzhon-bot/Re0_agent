namespace Re0Agent.Core.Services.Settings;

public sealed class RagContext
{
    public IReadOnlyList<RagMatch> Matches { get; init; } = [];
    public string Content { get; init; } = string.Empty;

    public int NonConstantCount => Matches.Count(match => !match.Entry.Constant);
}

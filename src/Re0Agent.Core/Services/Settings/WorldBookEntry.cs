namespace Re0Agent.Core.Services.Settings;

public sealed class WorldBookEntry
{
    public int Id { get; init; }
    public IReadOnlyList<string> Keys { get; init; } = [];
    public IReadOnlyList<string> SecondaryKeys { get; init; } = [];
    public string Comment { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public bool Constant { get; init; }
    public bool Selective { get; init; }
    public int InsertionOrder { get; init; }
    public bool Enabled { get; init; }
    public string? Position { get; init; }
    public bool UseRegex { get; init; }
}

namespace Re0Agent.Core.Models;

public sealed class CharacterAgentProfile
{
    public required string CharacterId { get; init; }
    public required string CharacterName { get; init; }
    public bool IsPlayerControlled { get; init; }
    public string? WorldBookEntryKey { get; init; }
    public string? CurrentStateReference { get; init; }
    public string? TemplateName { get; init; }
    public string? SystemPrompt { get; init; }
}

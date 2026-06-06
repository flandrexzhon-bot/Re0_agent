namespace Re0Agent.Core.Services.Dice;

public sealed class CharacterAttribute
{
    public required string CharacterName { get; init; }
    public required string AttributeName { get; init; }
    public int Value { get; init; }
    public bool IsPlayerControlled { get; init; }
}

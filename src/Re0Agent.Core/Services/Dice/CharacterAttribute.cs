namespace Re0Agent.Core.Services.Dice;

public sealed class CharacterAttribute
{
    public required string CharacterName { get; init; }
    public required string AttributeName { get; init; }
    public int Value { get; init; }
    public bool IsPlayerControlled { get; init; }

    /// <summary>角色稳定 ID（用于战斗直写定位）；0 表示未分配。</summary>
    public int CharId { get; init; }

    /// <summary>护甲值（战斗伤害减免）。</summary>
    public int Armor { get; init; }
}

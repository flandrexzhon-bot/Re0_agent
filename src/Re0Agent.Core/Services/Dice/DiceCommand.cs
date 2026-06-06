namespace Re0Agent.Core.Services.Dice;

public sealed class DiceCommand
{
    public required string RawText { get; init; }
    public DiceCommandKind Kind { get; init; }
    public string? RollerName { get; init; }
    public string? AttributeName { get; init; }
    public string? OpponentName { get; init; }
    public string? OpponentAttributeName { get; init; }
    public string Difficulty { get; init; } = "普通";
    public string? BonusPenalty { get; init; }
    public string MagicLevel { get; init; } = "基础";
    public string? GateAttributeName { get; init; }
    public bool? IsActive { get; init; }
    public string? AuthorityType { get; init; }
    public int? CurrentMiasmaLevel { get; init; }
    public bool CountersAuthority { get; init; }
    public bool ElementAdvantage { get; init; }
    public string? Error { get; init; }
}

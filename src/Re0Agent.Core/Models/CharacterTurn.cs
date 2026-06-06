namespace Re0Agent.Core.Models;

public sealed class CharacterTurn
{
    public required string RoundIndex { get; init; }
    public int OrderNumber { get; init; }
    public required string CharacterName { get; init; }
    public bool IsPlayerControlled { get; init; }
    public string? PlayerInstruction { get; set; }
    public string? ActionText { get; set; }
    public string? DiceCommand { get; set; }
    public string? GmJudgement { get; set; }
    public DiceResult? DiceResult { get; set; }
    public string? ResultResponse { get; set; }
    public bool Skipped { get; set; }
}

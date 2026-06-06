namespace Re0Agent.Core.Models;

public sealed class GameRound
{
    public required string RoundIndex { get; init; }
    public int Chapter { get; init; }
    public string? SceneSummary { get; init; }
    public string? GmOpening { get; set; }
    public string? PlayerInput { get; set; }
    public string? GmSummary { get; set; }
    public bool UsedFakeClient { get; set; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public IList<CharacterTurn> CharacterTurns { get; } = new List<CharacterTurn>();
    public IList<string> Events { get; } = new List<string>();
}

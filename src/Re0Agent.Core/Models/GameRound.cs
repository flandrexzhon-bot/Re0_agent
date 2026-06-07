namespace Re0Agent.Core.Models;

public sealed class GameRound
{
    public required string RoundIndex { get; init; }
    public int Chapter { get; set; }
    public string? SceneSummary { get; init; }
    public string? GmOpening { get; set; }
    public string? PlayerInput { get; set; }
    public string? GmSummary { get; set; }
    public bool UsedFakeClient { get; set; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public IList<CharacterTurn> CharacterTurns { get; set; } = new List<CharacterTurn>();
    public IList<string> Events { get; set; } = new List<string>();

    /// <summary>两阶段交互：BeginRoundAsync 后待执行的主角 profile（NPC 已先行动）。</summary>
    public IReadOnlyList<CharacterAgentProfile> PendingProtagonistProfiles { get; set; } = [];

    /// <summary>命中死亡回归时的死因；非空表示本回合触发死亡回归。</summary>
    public string? DeathReturnCause { get; set; }

    /// <summary>本回合是否已触发死亡回归（供前端播放转场动画）。</summary>
    public bool DeathReturnTriggered => DeathReturnCause is not null;
}

namespace Re0Agent.Core.Models;

public sealed class GameRound
{
    public required string RoundIndex { get; init; }
    public int Chapter { get; set; }
    public string? SceneSummary { get; init; }
    public string? GmOpening { get; set; }
    public string? PlayerInput { get; set; }
    public string? GmSummary { get; set; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public IList<CharacterTurn> CharacterTurns { get; set; } = new List<CharacterTurn>();
    public IList<string> Events { get; set; } = new List<string>();

    /// <summary>两阶段交互：BeginRoundAsync 后待执行的主角 profile（NPC 已先行动）。</summary>
    public IReadOnlyList<CharacterAgentProfile> PendingProtagonistProfiles { get; set; } = [];

    /// <summary>CharacterSub agent 输出的位号安排（在 GM 开场前确定）。</summary>
    public string? CharacterSubSlots { get; set; }

    /// <summary>上一回合及上上回合的原版内容（供历史上下文注入，由 GameProgressService 在启动回合前填入）。</summary>
    public IReadOnlyList<GameRound> PreviousRounds { get; init; } = [];

    /// <summary>命中死亡回归时的死因；非空表示本回合触发死亡回归。</summary>
    public string? DeathReturnCause { get; set; }

    /// <summary>本回合开始时所基于的存档锚点 ID（=上一回合末自动存档）。fork「引入叙事」回溯到此锚点；首回合为 null 时禁用 fork。</summary>
    public int? BaseSavePointId { get; set; }

    /// <summary>本回合是否已触发死亡回归（供前端播放转场动画）。</summary>
    public bool DeathReturnTriggered => DeathReturnCause is not null;
}

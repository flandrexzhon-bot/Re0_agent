namespace Re0Agent.Core.Models;

public enum RoundPhase
{
    Idle,
    GmRunning,
    NpcRunning,
    AwaitingPlayer,
    Finalizing,
    /// <summary>本回合在 GM 开场之后（主角已行动/部分 NPC 已回应）被手动停止，等待玩家「继续」恢复或重 roll。</summary>
    Interrupted
}

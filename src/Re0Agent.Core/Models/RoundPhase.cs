namespace Re0Agent.Core.Models;

/// <summary>
/// 大回合状态机。1–6 对应六段游戏流程，外加 Idle（无回合）与 Interrupted（3–6 段被停止）。
/// 该状态算法化持久到 chat_sessions.current_round_phase，重开 App 不靠猜。
/// </summary>
public enum RoundPhase
{
    /// <summary>无进行中回合，显示「▶ 开始大回合」。</summary>
    Idle = 0,

    /// <summary>1 GM 开场。</summary>
    GmOpening = 1,

    /// <summary>2 等玩家输入 —— 显示「书写主角的抉择」+ 输入框。</summary>
    AwaitingPlayer = 2,

    /// <summary>3 主角先行动（消费玩家输入）。</summary>
    ProtagonistActing = 3,

    /// <summary>4 调度 NPC（泉此方排位号）。</summary>
    NpcDispatching = 4,

    /// <summary>5 NPC 依位号回应。</summary>
    NpcResponding = 5,

    /// <summary>6 填表/存档。</summary>
    Finalizing = 6,

    /// <summary>在 3/4/5/6 段被手动停止，等待「继续」从断点恢复（断点段记于 interrupted_step）。</summary>
    Interrupted = 7
}

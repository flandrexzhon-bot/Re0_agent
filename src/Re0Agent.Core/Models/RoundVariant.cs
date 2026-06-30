using Re0Agent.Core.Entities;

namespace Re0Agent.Core.Models;

/// <summary>
/// 一次「整回合变体」——重 roll 产生。随会话持久化（见 <see cref="RoundVariantSet"/>），
/// 按 RoundIndex 长期保留；开新大回合不再清除旧回合的变体。
/// </summary>
public sealed class RoundVariant
{
    /// <summary>该变体的 GM 开场叙事。</summary>
    public string? GmOpening { get; set; }

    /// <summary>该变体的全部角色回合（主角 + NPC，按出场顺序）。</summary>
    public List<CharacterTurn> Turns { get; set; } = new();

    /// <summary>该变体的系统事件日志。</summary>
    public List<string> Events { get; set; } = new();

    /// <summary>该变体回合的完成时间（null = 仅开场未结算，如 AwaitingPlayer 阶段的开场变体）。</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>该变体回合结束（结算/填表后）的数据库快照，用于切回此变体时 restore。</summary>
    public required SavePoint DbSnapshot { get; set; }
}

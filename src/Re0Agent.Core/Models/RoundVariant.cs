using Re0Agent.Core.Entities;

namespace Re0Agent.Core.Models;

/// <summary>
/// 一次「整回合变体」——重 roll 产生。纯内存对象，仅在当前回合存活，
/// 不序列化进会话 JSON（DB 快照体量大）。开新大回合时整批释放。
/// </summary>
public sealed class RoundVariant
{
    /// <summary>该变体的 GM 开场叙事。</summary>
    public string? GmOpening { get; init; }

    /// <summary>该变体的全部角色回合（主角 + NPC，按出场顺序）。</summary>
    public List<CharacterTurn> Turns { get; init; } = new();

    /// <summary>该变体的系统事件日志。</summary>
    public List<string> Events { get; init; } = new();

    /// <summary>该变体回合结束（结算/填表后）的数据库快照，用于切回此变体时 restore。</summary>
    public required SavePoint DbSnapshot { get; init; }
}

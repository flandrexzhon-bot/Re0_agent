using Re0Agent.Core.Entities;

namespace Re0Agent.Core.Models;

/// <summary>
/// 一个大回合的全部「重 roll 变体」及其重 roll 所需的回档锚点。
/// 随会话持久化（存进 chat_sessions.round_variants_snapshot），按 RoundIndex 索引。
/// 这样每个回合的 roll 记录都长期保留，开新回合不再清空旧回合；fork 回到某回合即可重现其变体。
/// </summary>
public sealed class RoundVariantSet
{
    /// <summary>该回合的全部整回合变体（#0 为原版，按生成顺序）。</summary>
    public List<RoundVariant> Variants { get; set; } = new();

    /// <summary>当前激活的变体序号（0 基）。</summary>
    public int ActiveIndex { get; set; }

    /// <summary>该回合起点（GM 开场前）的 DB 快照。整局重跑 restore 到此。</summary>
    public SavePoint? RoundStartSnapshot { get; set; }

    /// <summary>该回合「每格开始前」的 DB 快照列表（含主角格）。逐格重 roll restore 到对应项。</summary>
    public List<SavePoint> PreTurnSnapshots { get; set; } = new();
}

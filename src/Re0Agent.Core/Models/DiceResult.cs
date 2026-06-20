namespace Re0Agent.Core.Models;

public sealed class DiceResult
{
    public required string Command { get; init; }
    public string? RollerName { get; init; }
    public string? AttributeName { get; init; }

    /// <summary>展示用骰值：2d6 之和（或既有 d100 特例的单值）。</summary>
    public int? Roll { get; init; }

    /// <summary>目标值 (DC)。</summary>
    public int? Target { get; init; }

    public required string Outcome { get; init; }
    public string? Detail { get; init; }
    public bool? IsSuccess { get; init; }
    public string? SuccessLevel { get; init; }
    public string? RequiredLevel { get; init; }
    public IReadOnlyList<int> Rolls { get; init; } = [];

    /// <summary>最终达成值 = 骰值 + 属性修正 + 加成（向后兼容旧字段名）。</summary>
    public int? TargetAfterModifiers { get; init; }

    public string? Error { get; init; }
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

    // ── V2 新增字段 ──
    /// <summary>2d6 原始点数之和（用于 12=大成功 / 2=大失败 判定）。</summary>
    public int? RawRollSum { get; init; }

    /// <summary>属性修正 = (属性值 - 10) / 2 向下取整。</summary>
    public int? AttributeModifier { get; init; }

    /// <summary>是否为战斗（攻击）判定。</summary>
    public bool IsCombat { get; init; }

    /// <summary>攻击者稳定 ID（战斗时）。</summary>
    public int? AttackerId { get; init; }

    /// <summary>防御者稳定 ID（战斗时）。</summary>
    public int? DefenderId { get; init; }

    /// <summary>命中后最终伤害（已扣护甲）。</summary>
    public int? Damage { get; init; }

    /// <summary>本次行动消耗的魔法值。</summary>
    public int ManaCost { get; init; }

    /// <summary>本次行动消耗的体力值。</summary>
    public int StaminaCost { get; init; }
}

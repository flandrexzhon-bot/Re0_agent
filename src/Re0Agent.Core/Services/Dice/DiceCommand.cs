namespace Re0Agent.Core.Services.Dice;

public sealed class DiceCommand
{
    public required string RawText { get; init; }
    public DiceCommandKind Kind { get; init; }

    // 行动方（可用 #ID 或名字定位）
    public string? RollerName { get; init; }
    public int? RollerId { get; init; }
    public string? AttributeName { get; init; }

    // 对抗/战斗的对方
    public string? OpponentName { get; init; }
    public int? OpponentId { get; init; }
    public string? OpponentAttributeName { get; init; }

    /// <summary>数值目标值 (DC)。检定/豁免与之对比。</summary>
    public int TargetValue { get; init; } = 10;

    /// <summary>状态/道具等额外加成（默认0）。</summary>
    public int SituationBonus { get; init; }

    // 战斗参数
    public string? SkillName { get; init; }
    public int WeaponDamage { get; init; }
    public int ManaCost { get; init; }
    public int StaminaCost { get; init; }

    // 豁免类型：魔法 / 精神 / 权能
    public string? SaveType { get; init; }

    // 既有特例
    public string? AuthorityType { get; init; }
    public int? CurrentMiasmaLevel { get; init; }

    public string? Error { get; init; }
}

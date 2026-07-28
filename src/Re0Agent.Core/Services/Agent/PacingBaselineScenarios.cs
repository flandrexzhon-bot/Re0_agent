namespace Re0Agent.Core.Services.Agent;

public sealed record PacingBaselineScenario(string Name, string Purpose, IReadOnlyList<string> EventTypes);

public static class PacingBaselineScenarios
{
    public static IReadOnlyList<PacingBaselineScenario> All { get; } =
    [
        new("quiet_arrival", "低压到场与人物建立", ["SceneOpportunity", "KeplerNarration"]),
        new("social_question", "角色主动询问主角", ["CharacterAction", "PlayerInput"]),
        new("escalating_threat", "威胁逐步升高", ["CharacterAction", "RuleResolution", "KeplerNarration"]),
        new("long_dialogue", "同角色连续发言抑制", ["CharacterAction", "CharacterAction", "CharacterAction"]),
        new("stalled_thread", "长期无状态变化后的推进债务", ["SceneOpportunity", "SceneOpportunity", "DirectorPlan"]),
        new("offscreen_consequence", "离屏事实进入当前区域", ["DistantWorldAdvance", "OffscreenRuleAdvance", "KeplerNarration"]),
        new("death_return", "死亡回归后的呼吸与重建", ["WorldRewindCommitted", "KeplerNarration", "CharacterAction"])
    ];
}

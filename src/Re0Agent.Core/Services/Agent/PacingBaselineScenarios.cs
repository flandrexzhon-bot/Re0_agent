namespace Re0Agent.Core.Services.Agent;

public sealed record PacingBaselineScenario(
    string Name,
    string Purpose,
    IReadOnlyList<string> EventTypes,
    IReadOnlyList<string> ExpectedSignals,
    IReadOnlyList<string> FailureSignals);

public static class PacingBaselineScenarios
{
    public static IReadOnlyList<PacingBaselineScenario> All { get; } =
    [
        new("quiet_arrival", "低压到场与人物建立", ["SceneOpportunity", "KeplerNarration"], ["low_tension", "camera_grounding"], ["instant_crisis"]),
        new("social_question", "角色主动询问主角", ["CharacterAction", "PlayerInput"], ["npc_agency", "player_contact"], ["player_only_trigger"]),
        new("escalating_threat", "威胁逐步升高", ["CharacterAction", "RuleResolution", "KeplerNarration"], ["causal_escalation", "consequence"], ["unearned_high"]),
        new("long_dialogue", "同角色连续发言抑制", ["CharacterAction", "CharacterAction", "CharacterAction"], ["speaker_handoff"], ["speaker_run_overflow"]),
        new("stalled_thread", "长期无状态变化后的推进债务", ["SceneOpportunity", "SceneOpportunity", "DirectorPlan"], ["fact_based_progress"], ["empty_filler"]),
        new("offscreen_consequence", "离屏事实进入当前区域", ["DistantWorldAdvance", "OffscreenRuleAdvance", "KeplerNarration"], ["forced_reveal", "visibility_gate"], ["hidden_consequence"]),
        new("death_return", "死亡回归后的呼吸与重建", ["WorldRewindCommitted", "KeplerNarration", "CharacterAction"], ["epoch_reset", "memory_scope"], ["cross_epoch_leak"])
    ];
}

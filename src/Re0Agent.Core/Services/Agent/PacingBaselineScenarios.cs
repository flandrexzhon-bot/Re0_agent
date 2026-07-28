using System.Text.Json;
using Re0Agent.Core.Entities;

namespace Re0Agent.Core.Services.Agent;

public sealed record PacingFixtureEvent(
    string EventType,
    string? ActorId,
    string SceneId,
    double Intensity,
    bool HasConsequence,
    string Content);

public sealed record PacingFixtureLabels(
    bool Dragging,
    bool Rushed,
    bool KnowledgeLeak,
    bool CharacterDrift,
    string Rationale);

public sealed record PacingExpectedRange(
    double MinimumTension,
    double MaximumTension,
    double MinimumConsequenceDensity,
    int MaximumSpeakerRun);

public sealed record PacingBaselineScenario(
    string Name,
    string Purpose,
    IReadOnlyList<PacingFixtureEvent> Events,
    PacingFixtureLabels Labels,
    PacingExpectedRange Expected,
    IReadOnlyList<string> ExpectedSignals,
    IReadOnlyList<string> FailureSignals);

public sealed record PacingFixtureEvaluation(
    string ScenarioName,
    bool Passed,
    PacingFeatures Features,
    IReadOnlyList<string> Failures);

public static class PacingBaselineScenarios
{
    public static IReadOnlyList<PacingBaselineScenario> All { get; } =
    [
        new("quiet_arrival", "低压到场与人物建立",
            [E("SceneOpportunity", null, .12), E("CharacterAction", "npc:1", .18), E("KeplerNarration", null, .16)],
            L(false, false, false, false, "低压开场应允许人物和空间先建立。"),
            R(0, .3, 0, 1), ["low_tension", "camera_grounding"], ["instant_crisis"]),
        new("active_social_contact", "角色不等玩家触发而主动询问",
            [E("CharacterAction", "npc:1", .25), E("CharacterAction", "npc:2", .3), E("KeplerNarration", null, .2)],
            L(false, false, false, false, "NPC 主动交流体现去主角中心自治。"),
            R(.1, .4, 0, 1), ["npc_agency", "player_contact"], ["player_only_trigger"]),
        new("earned_escalation", "威胁经过因果步骤逐步升高",
            [E("CharacterAction", "npc:1", .35), E("RuleResolution", "npc:1", .6, true), E("KeplerNarration", null, .72, true)],
            L(false, false, false, false, "高压由行动和规则后果逐步获得。"),
            R(.25, .7, .6, 1), ["causal_escalation", "consequence"], ["unearned_high"]),
        new("unearned_crisis", "识别没有铺垫的强度跳跃",
            [E("SceneOpportunity", null, .1), E("KeplerNarration", null, 1)],
            L(false, true, false, false, "低压事实后立即满强度属于节奏跳跃。"),
            R(.25, .5, 0, 0), ["rushed_label"], ["unearned_high"]),
        new("stalled_thread", "识别长期无状态变化的拖沓",
            [E("SceneOpportunity", null, .2), E("CharacterAction", "npc:1", .2), E("CharacterAction", "npc:2", .2), E("KeplerNarration", null, .2)],
            L(true, false, false, false, "连续事件没有状态后果，应累积推进债务。"),
            R(.05, .3, 0, 1), ["progress_debt"], ["empty_filler"]),
        new("speaker_monopoly", "识别同角色连续占据镜头",
            [E("CharacterAction", "npc:1", .3), E("CharacterAction", "npc:1", .3), E("CharacterAction", "npc:1", .3), E("CharacterAction", "npc:1", .3)],
            L(true, false, false, true, "同一角色连续四次行动会压制其他角色自治。"),
            R(.1, .4, 0, 3), ["speaker_handoff"], ["speaker_run_overflow"]),
        new("offscreen_leak", "识别 RP 中未经揭示的离屏事实",
            [E("DistantWorldAdvance", "npc:3", .45, true, "远方密谈已经完成。"), E("CharacterAction", "protagonist:1", .3, false, "主角直接说出密谈内容。")],
            L(false, false, true, false, "主角没有观察或揭示 cursor 却使用离屏事实。"),
            R(.15, .5, .4, 1), ["visibility_gate"], ["cross_scene_knowledge"]),
        new("death_return_drift", "识别回归后的跨 epoch 人设和记忆漂移",
            [E("WorldRewindCommitted", null, .8, true), E("CharacterAction", "npc:2", .35, false, "非回归者引用已回滚记忆。"), E("KeplerNarration", null, .25)],
            L(false, false, true, true, "非保留者引用旧 epoch，同时行为偏离当前可见状态。"),
            R(.2, .7, .3, 1), ["epoch_reset", "memory_scope"], ["cross_epoch_leak"])
    ];

    public static IReadOnlyList<TimelineEvent> Materialize(PacingBaselineScenario scenario) => scenario.Events
        .Select((item, index) => new TimelineEvent
        {
            EventId = $"fixture:{scenario.Name}:{index}",
            BranchId = $"fixture:{scenario.Name}",
            Sequence = index + 1,
            WorldEpoch = 1,
            SceneId = item.SceneId,
            ActorId = item.ActorId,
            EventType = item.EventType,
            Content = item.Content,
            StateChangeSet = item.HasConsequence ? "{\"commands\":[{}]}" : null,
            DirectObservers = "[]",
            PotentialLearners = "[]",
            ObservabilityComputed = 1,
            VisibilityScope = "[]",
            RevealedEventCursors = "[]",
            Status = "Committed",
            PacingMetadata = JsonSerializer.Serialize(new { intensity = item.Intensity }),
            RealCreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(index).ToString("O"),
            WorldTime = DateTimeOffset.UnixEpoch.AddSeconds(index).ToString("O")
        }).ToList();

    private static PacingFixtureEvent E(string type, string? actor, double intensity, bool consequence = false, string? content = null) =>
        new(type, actor, "fixture_scene", intensity, consequence, content ?? type);

    private static PacingFixtureLabels L(bool dragging, bool rushed, bool leak, bool drift, string rationale) =>
        new(dragging, rushed, leak, drift, rationale);

    private static PacingExpectedRange R(double minTension, double maxTension, double minConsequence, int maxSpeakerRun) =>
        new(minTension, maxTension, minConsequence, maxSpeakerRun);
}

public sealed class PacingFixtureEvaluator(PacingFeatureExtractor extractor)
{
    public IReadOnlyList<PacingFixtureEvaluation> EvaluateAll() => PacingBaselineScenarios.All.Select(Evaluate).ToList();

    public PacingFixtureEvaluation Evaluate(PacingBaselineScenario scenario)
    {
        var features = extractor.Extract(PacingBaselineScenarios.Materialize(scenario));
        var failures = new List<string>();
        if (features.Tension < scenario.Expected.MinimumTension || features.Tension > scenario.Expected.MaximumTension)
            failures.Add($"tension={features.Tension:F3}");
        if (features.ConsequenceDensity < scenario.Expected.MinimumConsequenceDensity)
            failures.Add($"consequence_density={features.ConsequenceDensity:F3}");
        if (!scenario.Labels.CharacterDrift && features.SpeakerRun > scenario.Expected.MaximumSpeakerRun)
            failures.Add($"speaker_run={features.SpeakerRun}");
        if (string.IsNullOrWhiteSpace(scenario.Labels.Rationale)) failures.Add("missing_label_rationale");
        return new PacingFixtureEvaluation(scenario.Name, failures.Count == 0, features, failures);
    }
}

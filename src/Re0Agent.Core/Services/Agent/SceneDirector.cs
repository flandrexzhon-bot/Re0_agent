using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Database;
using Re0Agent.Core.Services.Llm;

namespace Re0Agent.Core.Services.Agent;

public sealed class SceneDirector(
    Re0AgentDbContext dbContext,
    AgentConfigResolver configResolver,
    ILlmClient llmClient,
    PaceGovernor paceGovernor,
    EventSingleWriter eventWriter,
    WorldAffordanceValidator affordanceValidator)
{
    public async Task<BeatPlan> CreatePulseAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        var (candidates, decision) = await LoadCandidatesAsync(sessionId, cancellationToken);
        var plan = FallbackPlan(candidates, decision);
        Validate(plan, candidates, decision);
        return plan;
    }

    public async Task<BeatPlan> CreateFullPlanAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        var (candidates, decision) = await LoadCandidatesAsync(sessionId, cancellationToken);
        var config = await configResolver.FindConfigAsync("Director", "SceneDirector", cancellationToken);
        BeatPlan plan;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var response = await llmClient.SendChatAsync(new LlmRequest
            {
                AgentName = "SceneDirector",
                Options = AgentConfigResolver.ToLlmOptions(config),
                Messages = [LlmMessage.System(config?.SystemPrompt ?? "你是隐藏导演，只选择已发生事件和节奏边界，绝不创造事实或角色台词。"),
                    LlmMessage.User($"候选事实：{JsonSerializer.Serialize(candidates.Select(e => new { e.EventId, e.EventType, e.SceneId, e.Content }))}\n最大强度：{decision.MaximumIntensity}\n要求呼吸：{decision.RequiresBreathingBeat}\n要求推进：{decision.RequiresProgressBeat}\n仅输出 BeatPlan JSON。")]
            }, timeout.Token);
            plan = JsonSerializer.Deserialize<BeatPlan>(response.Content) ?? throw new InvalidOperationException("SceneDirector 未返回有效 BeatPlan。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            plan = FallbackPlan(candidates, decision);
        }
        catch (JsonException)
        {
            plan = FallbackPlan(candidates, decision);
        }
        Validate(plan, candidates, decision);
        await affordanceValidator.ValidateBeatPlanAsync(sessionId, plan, cancellationToken);
        await eventWriter.CommitAsync(sessionId, "DirectorPlan", JsonSerializer.Serialize(plan), triggerCause: "director_reflection", cancellationToken: cancellationToken);
        return plan;
    }

    private async Task<(IReadOnlyList<TimelineEvent> Candidates, PaceDecision Decision)> LoadCandidatesAsync(int sessionId, CancellationToken cancellationToken)
    {
        var session = await dbContext.ChatSessions.SingleAsync(item => item.SessionId == sessionId, cancellationToken);
        var candidates = await dbContext.TimelineEvents.AsNoTracking()
            .Where(item => item.BranchId == session.CurrentBranchId && item.Status == "Committed" && item.EventType != "DirectorPlan")
            .OrderByDescending(item => item.Sequence).Take(12).ToListAsync(cancellationToken);
        var state = await paceGovernor.ExtractAsync(session.CurrentBranchId!, candidates.OrderBy(item => item.Sequence).ToList(), cancellationToken);
        return (candidates, paceGovernor.Decide(state));
    }

    private static BeatPlan FallbackPlan(IReadOnlyList<TimelineEvent> candidates, PaceDecision decision) => new(
        Guid.NewGuid().ToString("N"),
        decision.RequiresProgressBeat ? "确定性推进" : decision.RequiresBreathingBeat ? "确定性呼吸" : "保持当前镜头",
        candidates.TakeLast(1).Select(item => item.EventId).ToList(),
        candidates.TakeLast(1).Where(item => item.ActorId is not null).Select(item => item.ActorId!).ToList(),
        candidates.LastOrDefault()?.SceneId,
        Math.Min(.3, decision.MaximumIntensity),
        "hold",
        [],
        "director_llm_timeout_or_invalid_response");

    private static void Validate(BeatPlan plan, IReadOnlyList<TimelineEvent> candidates, PaceDecision decision)
    {
        var ids = candidates.Select(item => item.EventId).ToHashSet(StringComparer.Ordinal);
        var candidateActors = candidates.Where(item => plan.CandidateEventIds.Contains(item.EventId)).Select(item => item.ActorId)
            .Where(item => item is not null).ToHashSet(StringComparer.Ordinal);
        var candidateScenes = candidates.Where(item => plan.CandidateEventIds.Contains(item.EventId)).Select(item => item.SceneId)
            .Where(item => item is not null).ToHashSet(StringComparer.Ordinal);
        if (plan.CandidateEventIds.Count == 0
            || plan.CandidateEventIds.Any(id => !ids.Contains(id))
            || plan.CandidateCharacterIds.Any(id => !candidateActors.Contains(id))
            || plan.ShotSuggestion is not null && !candidateScenes.Contains(plan.ShotSuggestion)
            || plan.TimeAdvancePolicy is not ("hold" or "advance_short" or "advance_until_next_opportunity")
            || plan.RequestedIntensity > decision.MaximumIntensity || plan.RequestedIntensity < 0)
            throw new InvalidOperationException("BeatPlan 超出候选事实或节奏边界。");
    }
}

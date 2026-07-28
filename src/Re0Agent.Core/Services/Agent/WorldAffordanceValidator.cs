using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Models;

namespace Re0Agent.Core.Services.Agent;

public sealed class WorldAffordanceValidator(Re0AgentDbContext dbContext)
{
    public async Task ValidateBeatPlanAsync(
        int sessionId,
        BeatPlan plan,
        CancellationToken cancellationToken = default)
    {
        var session = await dbContext.ChatSessions.AsNoTracking().SingleAsync(item => item.SessionId == sessionId, cancellationToken);
        var events = await dbContext.TimelineEvents.AsNoTracking()
            .Where(item => item.BranchId == session.CurrentBranchId && plan.CandidateEventIds.Contains(item.EventId) && item.Status == "Committed")
            .ToListAsync(cancellationToken);
        if (events.Count != plan.CandidateEventIds.Count)
            throw new InvalidOperationException("BeatPlan 包含不属于当前分支的事实。");
        if (plan.RevealBoundaries.Any(id => !plan.CandidateEventIds.Contains(id)))
            throw new InvalidOperationException("BeatPlan 的揭示边界没有事实来源。");
        foreach (var actorId in plan.CandidateCharacterIds)
        {
            var actorScene = await ReadActorSceneAsync(actorId, cancellationToken);
            if (actorScene is null || events.All(item => item.SceneId != actorScene))
                throw new InvalidOperationException($"BeatPlan 选择了当前不可达的角色：{actorId}");
        }
        if (plan.TimeAdvancePolicy == "advance_until_next_opportunity" && events.Count < 1)
            throw new InvalidOperationException("没有合法机会时不能推进到下一机会。");
    }

    public async Task ValidateAsync(
        CharacterAgentProfile profile,
        ActorBrief actorBrief,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(actorBrief.SceneId))
        {
            return;
        }
        var sceneExists = await dbContext.SceneStates.AsNoTracking().AnyAsync(item => item.SceneId == actorBrief.SceneId, cancellationToken);
        if (!sceneExists && actorBrief.SceneId != await dbContext.GlobalStates.AsNoTracking().Select(item => item.CurrentLocation).FirstOrDefaultAsync(cancellationToken))
        {
            throw new InvalidOperationException("行动机会引用了尚未成立的场景。");
        }
        var location = profile.CharacterId.StartsWith("protagonist:", StringComparison.Ordinal)
            ? await dbContext.ProtagonistInfo.AsNoTracking().Select(item => item.LocationName).FirstOrDefaultAsync(cancellationToken)
            : await dbContext.ImportantNpcs.AsNoTracking()
                .Where(item => $"npc:{item.RowId}" == profile.CharacterId)
                .Select(item => item.LocationName).FirstOrDefaultAsync(cancellationToken);
        if (!string.Equals(location, actorBrief.SceneId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("角色不在当前场景，不能获得当前镜头行动机会。");
        }
        if (!string.IsNullOrWhiteSpace(actorBrief.ContextEvent.TargetId))
        {
            var targetExists = actorBrief.ContextEvent.TargetId.StartsWith("npc:", StringComparison.Ordinal)
                ? await dbContext.ImportantNpcs.AnyAsync(item => $"npc:{item.RowId}" == actorBrief.ContextEvent.TargetId, cancellationToken)
                : actorBrief.ContextEvent.TargetId.StartsWith("protagonist:", StringComparison.Ordinal)
                    && await dbContext.ProtagonistInfo.AnyAsync(item => $"protagonist:{item.RowId}" == actorBrief.ContextEvent.TargetId, cancellationToken);
            if (!targetExists) throw new InvalidOperationException("行动机会引用了不存在的目标。");
        }
        if (string.IsNullOrWhiteSpace(actorBrief.Opportunity))
        {
            throw new InvalidOperationException("行动机会缺少可验证的动机。");
        }
    }

    private async Task<string?> ReadActorSceneAsync(string characterId, CancellationToken cancellationToken)
    {
        if (characterId.StartsWith("npc:", StringComparison.Ordinal))
            return await dbContext.ImportantNpcs.AsNoTracking().Where(item => $"npc:{item.RowId}" == characterId)
                .Select(item => item.LocationName).FirstOrDefaultAsync(cancellationToken);
        if (characterId.StartsWith("protagonist:", StringComparison.Ordinal))
            return await dbContext.ProtagonistInfo.AsNoTracking().Select(item => item.LocationName).FirstOrDefaultAsync(cancellationToken);
        return null;
    }
}

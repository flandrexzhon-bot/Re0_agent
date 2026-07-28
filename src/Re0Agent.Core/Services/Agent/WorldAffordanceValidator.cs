using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Models;

namespace Re0Agent.Core.Services.Agent;

public sealed class WorldAffordanceValidator(Re0AgentDbContext dbContext)
{
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
}

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
        var location = profile.CharacterId.StartsWith("protagonist:", StringComparison.Ordinal)
            ? await dbContext.ProtagonistInfo.AsNoTracking().Select(item => item.LocationName).FirstOrDefaultAsync(cancellationToken)
            : await dbContext.ImportantNpcs.AsNoTracking()
                .Where(item => $"npc:{item.RowId}" == profile.CharacterId)
                .Select(item => item.LocationName).FirstOrDefaultAsync(cancellationToken);
        if (!string.Equals(location, actorBrief.SceneId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("角色不在当前场景，不能获得当前镜头行动机会。");
        }
    }
}

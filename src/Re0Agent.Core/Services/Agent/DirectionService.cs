using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Database;

namespace Re0Agent.Core.Services.Agent;

public sealed class DirectionService(
    Re0AgentDbContext dbContext,
    DirectionDecomposer decomposer,
    EventSingleWriter eventWriter)
{
    public async Task<PlayerDirection?> RealizeNextAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        var pending = await dbContext.PendingDirections
            .Where(item => item.SessionId == sessionId && item.Status == "Pending")
            .OrderBy(item => item.DirectionId).FirstOrDefaultAsync(cancellationToken);
        if (pending is null)
        {
            return null;
        }

        pending.Status = "Realizing";
        await dbContext.SaveChangesAsync(cancellationToken);
        var direction = decomposer.Decompose(pending.DirectionId.ToString(), pending.Content);
        await eventWriter.CommitAsync(
            sessionId,
            "PlayerDirectionRealizing",
            JsonSerializer.Serialize(direction),
            triggerCause: "direction_decomposition",
            cancellationToken: cancellationToken);

        var blockedReason = await ValidateAsync(direction, cancellationToken);
        if (direction.Status == "Blocked" || blockedReason is not null)
        {
            pending.Status = "Blocked";
            direction = direction with { Status = "Blocked", BlockReason = blockedReason ?? direction.BlockReason };
            await dbContext.SaveChangesAsync(cancellationToken);
            return direction;
        }

        pending.Status = "Completed";
        await dbContext.SaveChangesAsync(cancellationToken);
        await eventWriter.CommitAsync(
            sessionId,
            "PlayerDirectionCompleted",
            JsonSerializer.Serialize(direction with { Status = "Completed" }),
            triggerCause: "direction_completed",
            cancellationToken: cancellationToken);
        return direction with { Status = "Completed" };
    }

    private async Task<string?> ValidateAsync(PlayerDirection direction, CancellationToken cancellationToken)
    {
        if (direction.RequestedScene is not null && !await dbContext.SceneStates.AnyAsync(item => item.SceneId == direction.RequestedScene, cancellationToken))
            return $"场景不存在：{direction.RequestedScene}";
        foreach (var actor in direction.RequestedActors)
        {
            var exists = await dbContext.ImportantNpcs.AnyAsync(item => item.Name == actor, cancellationToken)
                || await dbContext.ProtagonistInfo.AnyAsync(item => item.Name == actor, cancellationToken);
            if (!exists) return $"角色不存在：{actor}";
        }
        return null;
    }
}

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
    private static readonly TimeSpan MaximumRealizationAge = TimeSpan.FromHours(24);

    public async Task<PlayerDirection?> RealizeNextAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        var pending = await dbContext.PendingDirections
            .Where(item => item.SessionId == sessionId && (item.Status == "Pending" || item.Status == "Realizing"))
            .OrderBy(item => item.DirectionId).FirstOrDefaultAsync(cancellationToken);
        if (pending is null)
        {
            return null;
        }

        var direction = decomposer.Decompose(pending.DirectionId.ToString(), pending.Content);
        var session = await dbContext.ChatSessions.SingleAsync(item => item.SessionId == sessionId, cancellationToken);
        var currentWorldTime = session.WorldClockAnchor ?? DateTimeOffset.UtcNow.ToString("O");
        if (pending.Status == "Pending")
        {
            pending.Status = "Realizing";
            pending.Preconditions = JsonSerializer.Serialize(direction.Preconditions);
            pending.EarliestWorldTime = currentWorldTime;
            pending.CompletionProgress = 0;
            await dbContext.SaveChangesAsync(cancellationToken);
            await eventWriter.CommitAsync(
                sessionId,
                "PlayerDirectionRealizing",
                JsonSerializer.Serialize(direction),
                triggerCause: "direction_decomposition",
                causalParentEventId: pending.SourceEventId,
                cancellationToken: cancellationToken);
            if (direction.RequestedScene is not null)
            {
                await eventWriter.CommitAsync(
                    sessionId,
                    "DirectionSceneOpportunity",
                    JsonSerializer.Serialize(new { directionId = direction.DirectionId, sceneId = direction.RequestedScene }),
                    sceneId: direction.RequestedScene,
                    triggerCause: "player_direction_scene_opportunity",
                    causalParentEventId: pending.SourceEventId,
                    cancellationToken: cancellationToken);
            }
        }

        var blockedReason = await ValidateAsync(direction, pending, currentWorldTime, cancellationToken);
        if (direction.Status == "Blocked" || blockedReason is not null)
        {
            pending.Status = "Blocked";
            pending.BlockReason = blockedReason ?? direction.BlockReason;
            direction = direction with { Status = "Blocked", BlockReason = blockedReason ?? direction.BlockReason };
            await dbContext.SaveChangesAsync(cancellationToken);
            await eventWriter.CommitAsync(
                sessionId,
                "PlayerDirectionBlocked",
                JsonSerializer.Serialize(direction),
                triggerCause: "direction_precondition_failed",
                causalParentEventId: pending.SourceEventId,
                cancellationToken: cancellationToken);
            return direction;
        }

        pending.CompletionProgress = await CalculateProgressAsync(pending, direction, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (pending.CompletionProgress < 1)
        {
            return direction with { Status = "Realizing" };
        }
        pending.Status = "Completed";
        pending.BlockReason = null;
        await dbContext.SaveChangesAsync(cancellationToken);
        await eventWriter.CommitAsync(
            sessionId,
            "PlayerDirectionCompleted",
            JsonSerializer.Serialize(direction with { Status = "Completed" }),
            triggerCause: "direction_completed",
            cancellationToken: cancellationToken);
        return direction with { Status = "Completed" };
    }

    private async Task<string?> ValidateAsync(
        PlayerDirection direction,
        PendingDirection pending,
        string currentWorldTime,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> preconditions;
        try
        {
            preconditions = JsonSerializer.Deserialize<string[]>(pending.Preconditions) ?? direction.Preconditions;
        }
        catch (JsonException)
        {
            return "导演指令的前置条件链已损坏。";
        }
        foreach (var precondition in preconditions)
        {
            var separator = precondition.IndexOf(':');
            if (separator <= 0 || separator == precondition.Length - 1) return $"未知前置条件：{precondition}";
            var kind = precondition[..separator];
            var value = precondition[(separator + 1)..];
            if (kind == "scene_exists" && !await dbContext.SceneStates.AnyAsync(item => item.SceneId == value, cancellationToken))
                return $"场景不存在：{value}";
            if (kind == "actor_exists")
            {
                var exists = await dbContext.ImportantNpcs.AnyAsync(item => item.Name == value, cancellationToken)
                    || await dbContext.ProtagonistInfo.AnyAsync(item => item.Name == value, cancellationToken);
                if (!exists) return $"角色不存在：{value}";
            }
            if (kind is not ("scene_exists" or "actor_exists")) return $"不支持的前置条件：{kind}";
        }
        if (pending.CompletionProgress <= 0
            && DateTimeOffset.TryParse(pending.EarliestWorldTime, out var earliest)
            && DateTimeOffset.TryParse(currentWorldTime, out var current)
            && current - earliest >= MaximumRealizationAge)
            return "导演指令在 24 小时世界时间内没有产生可观察进展。";
        return null;
    }

    private async Task<double> CalculateProgressAsync(
        PendingDirection pending,
        PlayerDirection direction,
        CancellationToken cancellationToken)
    {
        var source = await dbContext.TimelineEvents.AsNoTracking().SingleAsync(item => item.EventId == pending.SourceEventId, cancellationToken);
        var subsequent = await dbContext.TimelineEvents.AsNoTracking()
            .Where(item => item.BranchId == source.BranchId && item.Status == "Committed" && item.Sequence > source.Sequence)
            .ToListAsync(cancellationToken);
        var conditions = 0;
        var satisfied = 0;
        if (direction.RequestedScene is not null)
        {
            conditions++;
            if (subsequent.Any(item => item.SceneId == direction.RequestedScene && (item.EventType is "CharacterAction" or "KeplerNarration"))) satisfied++;
        }
        foreach (var actor in direction.RequestedActors)
        {
            conditions++;
            var actorIds = await dbContext.ImportantNpcs.AsNoTracking().Where(item => item.Name == actor)
                .Select(item => $"npc:{item.RowId}").ToListAsync(cancellationToken);
            var protagonistId = await dbContext.ProtagonistInfo.AsNoTracking().Where(item => item.Name == actor)
                .Select(item => $"protagonist:{item.RowId}").FirstOrDefaultAsync(cancellationToken);
            if (protagonistId is not null) actorIds.Add(protagonistId);
            if (subsequent.Any(item => item.EventType == "CharacterAction" && item.ActorId is not null && actorIds.Contains(item.ActorId))) satisfied++;
        }
        if (conditions == 0)
        {
            conditions = 1;
            if (subsequent.Any(item => item.EventType is "CharacterAction" or "KeplerNarration")) satisfied = 1;
        }
        return satisfied / (double)conditions;
    }
}

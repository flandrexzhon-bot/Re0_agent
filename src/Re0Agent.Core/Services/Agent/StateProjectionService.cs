using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Services.Database;

namespace Re0Agent.Core.Services.Agent;

public sealed class StateProjectionService(
    Re0AgentDbContext dbContext,
    FormAgent formAgent,
    EventSingleWriter eventWriter,
    ObservabilityComputer observabilityComputer)
{
    public async Task<TimelineEvent> ProposeAndCommitAsync(
        int sessionId,
        TimelineEvent sourceEvent,
        CancellationToken cancellationToken = default)
    {
        var proposal = await formAgent.ProposeAsync(sourceEvent, await BuildSummaryAsync(cancellationToken), cancellationToken);
        var observers = await observabilityComputer.ComputeAsync(sourceEvent.SceneId, sourceEvent.ActorId, sourceEvent.TargetId, cancellationToken);
        return await eventWriter.CommitAsync(
            sessionId,
            "StateChangeCommitted",
            JsonSerializer.Serialize(new { sourceEventId = sourceEvent.EventId }),
            sceneId: sourceEvent.SceneId,
            actorId: sourceEvent.ActorId,
            targetId: sourceEvent.TargetId,
            stateChangeSet: proposal,
            observers: observers,
            triggerCause: "form_agent",
            causalParentEventId: sourceEvent.EventId,
            cancellationToken: cancellationToken);
    }

    private async Task<string> BuildSummaryAsync(CancellationToken cancellationToken)
    {
        var global = await dbContext.GlobalStates.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var npcs = await dbContext.ImportantNpcs.AsNoTracking().Select(item => new { item.RowId, item.Name, item.LocationName, item.SelfStatus })
            .ToListAsync(cancellationToken);
        return JsonSerializer.Serialize(new { global, protagonist, npcs });
    }
}

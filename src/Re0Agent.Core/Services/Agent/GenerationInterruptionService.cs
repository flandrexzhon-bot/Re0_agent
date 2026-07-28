using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Services.Database;

namespace Re0Agent.Core.Services.Agent;

public sealed class GenerationInterruptionService(
    Re0AgentDbContext dbContext,
    EventSingleWriter eventWriter,
    ObservabilityComputer observabilityComputer,
    WorldCancellationRegistry cancellationRegistry)
{
    public async Task<string> InterruptAsync(
        int sessionId,
        string displayedPrefix,
        string? sceneId,
        string? actorId,
        CancellationToken cancellationToken = default)
    {
        cancellationRegistry.CancelForeground(sessionId);
        var observers = await observabilityComputer.ComputeAsync(sceneId, actorId, null, cancellationToken);
        var interrupted = await eventWriter.CommitAsync(
            sessionId,
            "Interrupted",
            displayedPrefix,
            sceneId: sceneId,
            actorId: actorId,
            observers: observers,
            triggerCause: "player_interruption",
            eventStatus: "Interrupted",
            cancellationToken: cancellationToken);
        await dbContext.WorldSchedulerJobs.Where(item => item.SessionId == sessionId && item.Status == "Running")
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, "Interrupted"), cancellationToken);
        return interrupted.EventId;
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Models;

namespace Re0Agent.Core.Services.Agent;

public sealed class DirectorReflectionService(
    Re0AgentDbContext dbContext,
    SceneDirector sceneDirector)
{
    public async Task<BeatPlan?> ReflectIfTriggeredAsync(
        int sessionId,
        string triggerEventId,
        string triggerType,
        CancellationToken cancellationToken = default)
    {
        var session = await dbContext.ChatSessions.SingleAsync(item => item.SessionId == sessionId, cancellationToken);
        if (string.IsNullOrWhiteSpace(session.CurrentBranchId)) return null;
        var shouldReflect = triggerType is "PlayerDirectionRealizing" or "WorldRewindCommitted" or "InitialProjection";
        var hasVersion = await dbContext.DirectorPlanVersions.AnyAsync(item => item.BranchId == session.CurrentBranchId, cancellationToken);
        if (hasVersion && !shouldReflect) return null;
        var plan = await sceneDirector.CreatePulseAsync(sessionId, cancellationToken);
        dbContext.DirectorPlanVersions.Add(new Re0Agent.Core.Entities.DirectorPlanVersion
        {
            BranchId = session.CurrentBranchId,
            SourceEventId = triggerEventId,
            PlanJson = JsonSerializer.Serialize(plan),
            ReflectionReason = hasVersion ? triggerType : "first_plan",
            CreatedAt = DateTimeOffset.UtcNow.ToString("O")
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return plan;
    }
}

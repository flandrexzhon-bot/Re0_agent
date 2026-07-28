using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Services.Database;
using Re0Agent.Core.Models;
using System.Text.Json;

namespace Re0Agent.Core.Services.Agent;

public sealed class OffscreenSimulationService(
    Re0AgentDbContext dbContext,
    EventSingleWriter eventWriter,
    ObservabilityComputer observabilityComputer)
{
    private const int MaxAdvancesPerTick = 8;

    public async Task<int> AdvanceDueAsync(int sessionId, DateTimeOffset worldTime, CancellationToken cancellationToken = default)
    {
        var foregroundScene = await dbContext.ProtagonistInfo.AsNoTracking().Select(item => item.LocationName)
            .FirstOrDefaultAsync(cancellationToken);
        var due = await dbContext.CharacterAgencyStates
            .Where(item => item.Status == "Active" && item.SceneId != foregroundScene)
            .OrderBy(item => item.NextActionWorldTime).Take(MaxAdvancesPerTick).ToListAsync(cancellationToken);
        var advanced = 0;
        foreach (var agency in due)
        {
            if (!DateTimeOffset.TryParse(agency.NextActionWorldTime, out var scheduled) || scheduled > worldTime) continue;
            if (agency.Fidelity == "regional")
            {
                var observers = await observabilityComputer.ComputeAsync(agency.SceneId, agency.CharacterId, null, cancellationToken, "OffscreenRuleAdvance");
                await eventWriter.CommitAsync(sessionId, "OffscreenRuleAdvance",
                    $"{{\"characterId\":\"{agency.CharacterId}\",\"goal\":{System.Text.Json.JsonSerializer.Serialize(agency.CurrentGoal)}}}",
                    sceneId: agency.SceneId, actorId: agency.CharacterId, observers: observers,
                    stateChangeSet: NextActionChange(agency.CharacterId, worldTime),
                    triggerCause: "regional_offscreen_schedule", cancellationToken: cancellationToken);
            }
            else
            {
                await eventWriter.CommitAsync(sessionId, "DistantWorldAdvance",
                    $"{{\"characterId\":\"{agency.CharacterId}\",\"fidelity\":\"distant\"}}",
                    sceneId: agency.SceneId, actorId: agency.CharacterId,
                    stateChangeSet: NextActionChange(agency.CharacterId, worldTime),
                    triggerCause: "distant_offscreen_schedule", cancellationToken: cancellationToken);
            }
            advanced++;
        }
        return advanced;
    }

    public async Task<int> PromoteForForegroundAsync(
        int sessionId,
        string foregroundSceneId,
        DateTimeOffset worldTime,
        CancellationToken cancellationToken = default)
    {
        var session = await dbContext.ChatSessions.AsNoTracking().SingleAsync(item => item.SessionId == sessionId, cancellationToken);
        var foreground = await dbContext.SceneStates.AsNoTracking().SingleOrDefaultAsync(item => item.SceneId == foregroundSceneId, cancellationToken);
        if (foreground is null) return 0;
        var sceneRegions = await dbContext.SceneStates.AsNoTracking().ToDictionaryAsync(item => item.SceneId, item => item.RegionId, cancellationToken);
        var agencies = await dbContext.CharacterAgencyStates.Where(item => item.Status == "Active" && item.Fidelity != "foreground")
            .OrderBy(item => item.CharacterId).ToListAsync(cancellationToken);
        var promoted = 0;
        foreach (var agency in agencies)
        {
            var target = agency.SceneId == foregroundSceneId
                ? "foreground"
                : agency.Fidelity == "distant" && agency.SceneId is not null
                    && sceneRegions.GetValueOrDefault(agency.SceneId) == foreground.RegionId ? "regional" : null;
            if (target is null || FidelityRank(target) <= FidelityRank(agency.Fidelity)) continue;
            var latestFact = await dbContext.TimelineEvents.AsNoTracking().Where(item => item.BranchId == session.CurrentBranchId
                    && item.ActorId == agency.CharacterId && item.Status == "Committed"
                    && (item.EventType == "DistantWorldAdvance" || item.EventType == "OffscreenRuleAdvance" || item.EventType == "CharacterAction"))
                .OrderByDescending(item => item.Sequence).FirstOrDefaultAsync(cancellationToken);
            var observers = await observabilityComputer.ComputeAsync(
                agency.SceneId, agency.CharacterId, null, cancellationToken, "OffscreenFidelityPromoted");
            await eventWriter.CommitAsync(
                sessionId,
                "OffscreenFidelityPromoted",
                JsonSerializer.Serialize(new
                {
                    characterId = agency.CharacterId,
                    from = agency.Fidelity,
                    to = target,
                    restoredThroughEventId = latestFact?.EventId
                }),
                sceneId: agency.SceneId,
                actorId: agency.CharacterId,
                stateChangeSet: FidelityChange(agency.CharacterId, target, worldTime),
                observers: observers,
                triggerCause: "deterministic_fidelity_promotion",
                causalParentEventId: latestFact?.EventId,
                pacingMetadata: "{\"importance\":0.8,\"intensity\":0.35}",
                cancellationToken: cancellationToken);
            promoted++;
        }
        return promoted;
    }

    private static StateChangeSet NextActionChange(string characterId, DateTimeOffset worldTime) => new(
    [
        new StateChangeCommand(
            Guid.NewGuid().ToString("N"),
            "character_agency_states",
            characterId,
            "update",
            null,
            new Dictionary<string, JsonElement>
            {
                ["next_action_world_time"] = JsonSerializer.SerializeToElement(worldTime.AddMinutes(10).ToString("O"))
            },
            "offscreen_schedule")
    ]);

    private static StateChangeSet FidelityChange(string characterId, string fidelity, DateTimeOffset worldTime) => new(
    [
        new StateChangeCommand(
            Guid.NewGuid().ToString("N"),
            "character_agency_states",
            characterId,
            "update",
            null,
            new Dictionary<string, JsonElement>
            {
                ["fidelity"] = JsonSerializer.SerializeToElement(fidelity),
                ["next_action_world_time"] = JsonSerializer.SerializeToElement(worldTime.ToString("O"))
            },
            "offscreen_fidelity_promotion")
    ]);

    private static int FidelityRank(string fidelity) => fidelity switch
    {
        "distant" => 0,
        "regional" => 1,
        "foreground" => 2,
        _ => -1
    };
}

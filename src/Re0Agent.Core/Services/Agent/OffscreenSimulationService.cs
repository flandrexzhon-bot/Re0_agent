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
                    stateChangeSet: NextActionChange(agency.CharacterId, worldTime),
                    triggerCause: "distant_offscreen_schedule", cancellationToken: cancellationToken);
            }
            advanced++;
        }
        return advanced;
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
}

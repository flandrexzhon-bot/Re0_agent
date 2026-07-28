using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;
using System.Text.Json;

namespace Re0Agent.Core.Services.Agent;

public sealed record WorldCandidate(string CharacterId, string SceneId, ActorMotivation Motivation, DateTimeOffset EarliestWorldTime);

public sealed class WorldCandidateBuilder(Re0AgentDbContext dbContext)
{
    public async Task<IReadOnlyList<WorldCandidate>> BuildCurrentSceneAsync(
        ChatSession session,
        string sceneId,
        DateTimeOffset worldTime,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<WorldCandidate>();
        var directedNames = new HashSet<string>(StringComparer.Ordinal);
        var directionChains = await dbContext.PendingDirections.AsNoTracking().Where(item => item.SessionId == session.SessionId
                && (item.Status == "Pending" || item.Status == "Realizing"))
            .Select(item => item.Preconditions).ToListAsync(cancellationToken);
        foreach (var chain in directionChains)
        {
            try
            {
                foreach (var condition in JsonSerializer.Deserialize<string[]>(chain) ?? [])
                    if (condition.StartsWith("actor_exists:", StringComparison.Ordinal)) directedNames.Add(condition[13..]);
            }
            catch (JsonException) { }
        }
        var directedIds = (await dbContext.ImportantNpcs.AsNoTracking().Where(item => directedNames.Contains(item.Name))
            .Select(item => $"npc:{item.RowId}").ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var agencies = await dbContext.CharacterAgencyStates.AsNoTracking()
            .Where(item => item.Status == "Active" && item.SceneId == sceneId)
            .ToListAsync(cancellationToken);
        foreach (var agency in agencies)
        {
            if (DateTimeOffset.TryParse(agency.NextActionWorldTime, out var next) && next <= worldTime)
                candidates.Add(new WorldCandidate(agency.CharacterId, sceneId,
                    Motivation(agency.CurrentGoal ?? "依据自身目标行动。", directedIds.Contains(agency.CharacterId) ? "high" : "medium",
                        directedIds.Contains(agency.CharacterId) ? "player_direction" : "character_agency_state"), next));
        }

        var npcIds = await dbContext.ImportantNpcs.AsNoTracking().Where(item => item.LocationName == sceneId)
            .Select(item => $"npc:{item.RowId}").ToListAsync(cancellationToken);
        var existing = candidates.Select(item => item.CharacterId).ToHashSet(StringComparer.Ordinal);
        foreach (var npcId in npcIds.Where(id => !existing.Contains(id)))
        {
            var recent = await dbContext.WorldSchedulerJobs.AsNoTracking()
                .Where(item => item.SessionId == session.SessionId && item.JobType == "CharacterAgency" && item.Payload != null && item.Status == "Completed")
                .OrderByDescending(item => item.JobId).Take(8).ToListAsync(cancellationToken);
            if (recent.Any(item => item.Payload!.Contains($"\"CharacterId\":\"{npcId}\"", StringComparison.OrdinalIgnoreCase)
                && DateTimeOffset.TryParse(item.ScheduledWorldTime, out var scheduled)
                && scheduled.AddMinutes(20) > worldTime)) continue;
            candidates.Add(new WorldCandidate(npcId, sceneId,
                Motivation("根据自身当前目标与已知事实采取行动。", directedIds.Contains(npcId) ? "high" : "low",
                    directedIds.Contains(npcId) ? "player_direction" : "deterministic_default"), worldTime));
        }
        if (session.GameMode == "Theater")
        {
            var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
            if (protagonist?.LocationName == sceneId)
                candidates.Add(new WorldCandidate($"protagonist:{protagonist.RowId}", sceneId,
                    Motivation("根据主角的自治目标行动。", "medium", "theater_protagonist_agency"), worldTime));
        }
        return candidates;
    }

    private static ActorMotivation Motivation(string value, string urgency, string source) => new(
        value,
        urgency,
        ["speak", "move", "interact", "wait"],
        [],
        source);
}

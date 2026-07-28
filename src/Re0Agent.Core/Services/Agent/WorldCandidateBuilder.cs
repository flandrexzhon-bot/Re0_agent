using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;

namespace Re0Agent.Core.Services.Agent;

public sealed record WorldCandidate(string CharacterId, string SceneId, string Opportunity, DateTimeOffset EarliestWorldTime);

public sealed class WorldCandidateBuilder(Re0AgentDbContext dbContext)
{
    public async Task<IReadOnlyList<WorldCandidate>> BuildCurrentSceneAsync(
        ChatSession session,
        string sceneId,
        DateTimeOffset worldTime,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<WorldCandidate>();
        var agencies = await dbContext.CharacterAgencyStates.AsNoTracking()
            .Where(item => item.Status == "Active" && item.SceneId == sceneId)
            .ToListAsync(cancellationToken);
        foreach (var agency in agencies)
        {
            if (DateTimeOffset.TryParse(agency.NextActionWorldTime, out var next) && next <= worldTime)
                candidates.Add(new WorldCandidate(agency.CharacterId, sceneId, agency.CurrentGoal ?? "依据自身目标行动。", next));
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
            candidates.Add(new WorldCandidate(npcId, sceneId, "根据自身当前目标与已知事实采取行动。", worldTime));
        }
        if (session.GameMode == "Theater")
        {
            var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
            if (protagonist?.LocationName == sceneId)
                candidates.Add(new WorldCandidate($"protagonist:{protagonist.RowId}", sceneId, "根据主角的自治目标行动。", worldTime));
        }
        return candidates;
    }
}

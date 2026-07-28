using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Models;

namespace Re0Agent.Core.Services.Database;

public sealed class ObservabilityComputer(Re0AgentDbContext dbContext)
{
    public async Task<DirectObserversResult> ComputeAsync(
        string? sceneId,
        string? actorId,
        string? targetId,
        CancellationToken cancellationToken = default)
    {
        var observers = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(actorId)) observers.Add(actorId);
        if (!string.IsNullOrWhiteSpace(targetId)) observers.Add(targetId);
        if (!string.IsNullOrWhiteSpace(sceneId))
        {
            var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
            if (protagonist?.LocationName == sceneId)
            {
                observers.Add($"protagonist:{protagonist.RowId}");
            }
            var npcs = await dbContext.ImportantNpcs.AsNoTracking()
                .Where(item => item.LocationName == sceneId).Select(item => item.RowId).ToListAsync(cancellationToken);
            foreach (var rowId in npcs) observers.Add($"npc:{rowId}");
        }

        var potential = new List<PotentialLearner>();
        if (!string.IsNullOrWhiteSpace(sceneId))
        {
            var regionId = await dbContext.SceneStates.AsNoTracking().Where(item => item.SceneId == sceneId)
                .Select(item => item.RegionId).FirstOrDefaultAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(regionId))
            {
                var regionalIds = await (from agency in dbContext.CharacterAgencyStates.AsNoTracking()
                    join scene in dbContext.SceneStates.AsNoTracking() on agency.SceneId equals scene.SceneId
                    where agency.Status == "Active" && scene.RegionId == regionId
                    select agency.CharacterId).ToListAsync(cancellationToken);
                potential.AddRange(regionalIds.Where(id => !observers.Contains(id)).Distinct(StringComparer.Ordinal)
                    .Select(id => new PotentialLearner(id, "regional_report", "after_report_or_encounter")));
            }
        }

        return new DirectObserversResult(
            observers.OrderBy(item => item, StringComparer.Ordinal).ToList(),
            observers.ToDictionary(item => item, _ => "same_scene", StringComparer.Ordinal),
            potential,
            true);
    }
}

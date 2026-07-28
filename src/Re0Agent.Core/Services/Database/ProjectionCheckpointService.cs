using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;

namespace Re0Agent.Core.Services.Database;

public sealed class ProjectionCheckpointService(Re0AgentDbContext dbContext)
{
    public async Task CaptureAsync(string branchId, long eventSequence, int worldEpoch, CancellationToken cancellationToken = default)
    {
        if (await dbContext.ProjectionCheckpoints.AnyAsync(item => item.BranchId == branchId && item.EventSequence == eventSequence && item.WorldEpoch == worldEpoch, cancellationToken)) return;
        var snapshot = new ProjectionSnapshot(
            await dbContext.GlobalStates.AsNoTracking().ToListAsync(cancellationToken),
            await dbContext.ProtagonistInfo.AsNoTracking().ToListAsync(cancellationToken),
            await dbContext.WorldMapPoints.AsNoTracking().ToListAsync(cancellationToken),
            await dbContext.MapElements.AsNoTracking().ToListAsync(cancellationToken),
            await dbContext.Factions.AsNoTracking().ToListAsync(cancellationToken),
            await dbContext.ImportantNpcs.AsNoTracking().ToListAsync(cancellationToken),
            await dbContext.Inventory.AsNoTracking().ToListAsync(cancellationToken),
            await dbContext.Equipment.AsNoTracking().ToListAsync(cancellationToken),
            await dbContext.Quests.AsNoTracking().ToListAsync(cancellationToken),
            await dbContext.Chronicle.AsNoTracking().ToListAsync(cancellationToken),
            await dbContext.CharacterMemory.AsNoTracking().ToListAsync(cancellationToken),
            await dbContext.SceneStates.AsNoTracking().ToListAsync(cancellationToken),
            await dbContext.CharacterAgencyStates.AsNoTracking().ToListAsync(cancellationToken),
            await dbContext.StoryThreads.AsNoTracking().ToListAsync(cancellationToken),
            await dbContext.ProjectionEntityVersions.AsNoTracking().ToListAsync(cancellationToken),
            await dbContext.ProjectionCommandLogs.AsNoTracking().ToListAsync(cancellationToken));
        dbContext.ProjectionCheckpoints.Add(new ProjectionCheckpoint
        {
            BranchId = branchId,
            EventSequence = eventSequence,
            WorldEpoch = worldEpoch,
            ProjectionData = JsonSerializer.Serialize(snapshot),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O")
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<long?> TryRestoreNearestAsync(string branchId, long targetSequence, CancellationToken cancellationToken = default)
    {
        var checkpoint = await dbContext.ProjectionCheckpoints.AsNoTracking()
            .Where(item => item.BranchId == branchId && item.EventSequence <= targetSequence)
            .OrderByDescending(item => item.EventSequence).FirstOrDefaultAsync(cancellationToken);
        if (checkpoint is null) return null;
        ProjectionSnapshot? snapshot;
        try { snapshot = JsonSerializer.Deserialize<ProjectionSnapshot>(checkpoint.ProjectionData); }
        catch (JsonException) { return null; }
        if (snapshot is null || snapshot.SceneStates is null || snapshot.CharacterAgencyStates is null || snapshot.StoryThreads is null) return null;
        await ClearAsync(cancellationToken);
        dbContext.AddRange(snapshot.GlobalStates);
        dbContext.AddRange(snapshot.Protagonists);
        dbContext.AddRange(snapshot.WorldMapPoints);
        dbContext.AddRange(snapshot.MapElements);
        dbContext.AddRange(snapshot.Factions);
        dbContext.AddRange(snapshot.ImportantNpcs);
        dbContext.AddRange(snapshot.Inventory);
        dbContext.AddRange(snapshot.Equipment);
        dbContext.AddRange(snapshot.Quests);
        dbContext.AddRange(snapshot.Chronicle);
        dbContext.AddRange(snapshot.CharacterMemory);
        dbContext.AddRange(snapshot.SceneStates);
        dbContext.AddRange(snapshot.CharacterAgencyStates);
        dbContext.AddRange(snapshot.StoryThreads);
        dbContext.AddRange(snapshot.EntityVersions);
        dbContext.AddRange(snapshot.CommandLogs);
        await dbContext.SaveChangesAsync(cancellationToken);
        return checkpoint.EventSequence;
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await dbContext.MemoryEmbeddings.ExecuteDeleteAsync(cancellationToken);
        await dbContext.PacingStateCache.ExecuteDeleteAsync(cancellationToken);
        await dbContext.CharacterMemory.ExecuteDeleteAsync(cancellationToken);
        await dbContext.StoryThreads.ExecuteDeleteAsync(cancellationToken);
        await dbContext.CharacterAgencyStates.ExecuteDeleteAsync(cancellationToken);
        await dbContext.SceneStates.ExecuteDeleteAsync(cancellationToken);
        await dbContext.Chronicle.ExecuteDeleteAsync(cancellationToken);
        await dbContext.Quests.ExecuteDeleteAsync(cancellationToken);
        await dbContext.Equipment.ExecuteDeleteAsync(cancellationToken);
        await dbContext.Inventory.ExecuteDeleteAsync(cancellationToken);
        await dbContext.ImportantNpcs.ExecuteDeleteAsync(cancellationToken);
        await dbContext.ProtagonistInfo.ExecuteDeleteAsync(cancellationToken);
        await dbContext.Factions.ExecuteDeleteAsync(cancellationToken);
        await dbContext.MapElements.ExecuteDeleteAsync(cancellationToken);
        await dbContext.WorldMapPoints.ExecuteDeleteAsync(cancellationToken);
        await dbContext.GlobalStates.ExecuteDeleteAsync(cancellationToken);
        await dbContext.ProjectionCommandLogs.ExecuteDeleteAsync(cancellationToken);
        await dbContext.ProjectionEntityVersions.ExecuteDeleteAsync(cancellationToken);
    }

    private sealed record ProjectionSnapshot(
        List<GlobalState> GlobalStates,
        List<ProtagonistInfo> Protagonists,
        List<WorldMapPoint> WorldMapPoints,
        List<MapElement> MapElements,
        List<Faction> Factions,
        List<ImportantNpc> ImportantNpcs,
        List<InventoryItem> Inventory,
        List<EquipmentItem> Equipment,
        List<Quest> Quests,
        List<ChronicleEntry> Chronicle,
        List<CharacterMemory> CharacterMemory,
        List<SceneState> SceneStates,
        List<CharacterAgencyState> CharacterAgencyStates,
        List<StoryThread> StoryThreads,
        List<ProjectionEntityVersion> EntityVersions,
        List<ProjectionCommandLog> CommandLogs);
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;

namespace Re0Agent.Core.Services.Database;

public sealed class ProjectionReplayer(
    Re0AgentDbContext dbContext,
    ProjectionCommandApplier projectionCommandApplier,
    ProjectionCheckpointService checkpointService)
{
    public async Task ReplayToSavePointAsync(
        SavePoint savePoint,
        IReadOnlyCollection<string> memoryRetainers,
        CancellationToken cancellationToken = default)
    {
        var retainedMemories = await CaptureRetainedMemoriesAsync(memoryRetainers, cancellationToken);
        await ReplayToEventAsync(savePoint.BranchId, savePoint.EventId, cancellationToken);
        await RestoreRetainedMemoriesAsync(retainedMemories, cancellationToken);
    }

    public async Task ReplayToEventAsync(
        string branchId,
        string targetEventId,
        CancellationToken cancellationToken = default)
    {
        var targetSequence = await dbContext.TimelineEvents.Where(item => item.EventId == targetEventId && item.BranchId == branchId)
            .Select(item => item.Sequence).SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("找不到目标分支上的重放事件。");
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var checkpointSequence = await checkpointService.TryRestoreNearestAsync(branchId, targetSequence, cancellationToken);
        IReadOnlyList<TimelineEvent> events;
        if (checkpointSequence is null)
        {
            await checkpointService.ClearAsync(cancellationToken);
            events = await ReadEffectiveEventsAsync(branchId, targetEventId, cancellationToken);
        }
        else
        {
            events = await dbContext.TimelineEvents.AsNoTracking()
                .Where(item => item.BranchId == branchId && item.Status == "Committed" && item.Sequence > checkpointSequence && item.Sequence <= targetSequence)
                .OrderBy(item => item.Sequence).ToListAsync(cancellationToken);
        }
        foreach (var eventRecord in events)
        {
            if (string.IsNullOrWhiteSpace(eventRecord.StateChangeSet))
            {
                continue;
            }

            var changes = JsonSerializer.Deserialize<StateChangeSet>(eventRecord.StateChangeSet)
                ?? throw new InvalidOperationException("事件包含无法解析的 StateChangeSet。");
            await projectionCommandApplier.ApplyAsync(eventRecord.EventId, changes, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<TimelineEvent>> ReadEffectiveEventsAsync(
        string branchId,
        string targetEventId,
        CancellationToken cancellationToken)
    {
        var lineage = new Stack<(string BranchId, long? EndSequence)>();
        var currentBranchId = branchId;
        long? currentEnd = await dbContext.TimelineEvents.Where(item => item.EventId == targetEventId)
            .Select(item => item.Sequence).SingleOrDefaultAsync(cancellationToken);
        if (currentEnd is null)
        {
            throw new InvalidOperationException("找不到重放目标事件。");
        }

        while (true)
        {
            lineage.Push((currentBranchId, currentEnd));
            var branch = await dbContext.TimelineBranches.SingleAsync(item => item.BranchId == currentBranchId, cancellationToken);
            if (string.IsNullOrWhiteSpace(branch.ParentBranchId))
            {
                break;
            }
            if (string.IsNullOrWhiteSpace(branch.ParentEventId))
            {
                throw new InvalidOperationException("时间线分支缺少父事件游标。");
            }
            currentBranchId = branch.ParentBranchId;
            currentEnd = await dbContext.TimelineEvents.Where(item => item.EventId == branch.ParentEventId)
                .Select(item => item.Sequence).SingleOrDefaultAsync(cancellationToken);
            if (currentEnd is null)
            {
                throw new InvalidOperationException("时间线分支的父事件不存在。");
            }
        }

        var result = new List<TimelineEvent>();
        while (lineage.Count > 0)
        {
            var (lineageBranchId, endSequence) = lineage.Pop();
            result.AddRange(await dbContext.TimelineEvents.AsNoTracking()
                .Where(item => item.BranchId == lineageBranchId
                    && item.Status == "Committed"
                    && item.Sequence != null
                    && item.Sequence <= endSequence)
                .OrderBy(item => item.Sequence)
                .ToListAsync(cancellationToken));
        }
        return result;
    }

    private async Task<IReadOnlyList<CharacterMemory>> CaptureRetainedMemoriesAsync(
        IReadOnlyCollection<string> memoryRetainers,
        CancellationToken cancellationToken)
    {
        if (memoryRetainers.Count == 0)
        {
            return [];
        }
        return await dbContext.CharacterMemory.AsNoTracking()
            .Where(item => memoryRetainers.Contains(item.OwnerCharacterId) && item.RetainOnRewind == 1)
            .ToListAsync(cancellationToken);
    }

    private async Task RestoreRetainedMemoriesAsync(
        IReadOnlyList<CharacterMemory> retainedMemories,
        CancellationToken cancellationToken)
    {
        if (retainedMemories.Count == 0)
        {
            return;
        }
        var existingSourceIds = await dbContext.CharacterMemory.AsNoTracking()
            .Select(item => item.SourceEventId).ToListAsync(cancellationToken);
        var nextId = (await dbContext.CharacterMemory.MaxAsync(item => (int?)item.RowId, cancellationToken) ?? 0) + 1;
        var additions = retainedMemories.Where(item => !existingSourceIds.Contains(item.SourceEventId, StringComparer.Ordinal))
            .Select(item => new CharacterMemory
            {
                RowId = nextId++,
                OwnerCharacterId = item.OwnerCharacterId,
                SourceEventId = item.SourceEventId,
                WorldTime = item.WorldTime,
                WorldEpoch = item.WorldEpoch,
                ObservationChannel = item.ObservationChannel,
                Confidence = item.Confidence,
                VisibilityScope = item.VisibilityScope,
                MemoryType = item.MemoryType,
                RetainOnRewind = 1,
                MemoryText = item.MemoryText,
                EmotionalState = item.EmotionalState,
                CreatedAt = item.CreatedAt
            }).ToList();
        if (additions.Count > 0)
        {
            dbContext.CharacterMemory.AddRange(additions);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}

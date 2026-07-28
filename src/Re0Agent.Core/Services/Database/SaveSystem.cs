using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Dice;

namespace Re0Agent.Core.Services.Database;

public sealed record SavePointSummary(
    int SaveId,
    string EventId,
    int WorldEpoch,
    string TriggerReason,
    string CreatedAt,
    bool IsLatest);

public sealed record DeathReturnResult(
    int SavePointId,
    int LoopCount,
    string DeathCause,
    int MiasmaLevel,
    string Detail);

public sealed class SaveSystem(
    Re0AgentDbContext dbContext,
    DiceEngine diceEngine,
    EventSingleWriter eventWriter,
    ProjectionReplayer projectionReplayer)
{
    public async Task<SavePointSummary> CreateSavePointAsync(
        string triggerReason,
        CancellationToken cancellationToken = default)
    {
        await DatabaseInitializer.InitializeAsync(dbContext, cancellationToken);
        var session = await dbContext.ChatSessions.SingleOrDefaultAsync(item => item.IsActive == 1, cancellationToken)
            ?? throw new InvalidOperationException("没有活动会话，无法创建事件书签。");
        if (string.IsNullOrWhiteSpace(session.CurrentBranchId))
        {
            throw new InvalidOperationException("活动会话缺少时间线分支。");
        }

        var eventRecord = await dbContext.TimelineEvents
            .Where(item => item.BranchId == session.CurrentBranchId && item.Status == "Committed")
            .OrderByDescending(item => item.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
        if (eventRecord is null)
        {
            eventRecord = await eventWriter.CommitAsync(
                session.SessionId,
                "InitialProjection",
                "{}",
                triggerCause: "initial_state",
                cancellationToken: cancellationToken);
        }

        var savePoint = new SavePoint
        {
            BranchId = session.CurrentBranchId,
            EventId = eventRecord.EventId,
            WorldEpoch = session.CurrentWorldEpoch,
            TriggerReason = string.IsNullOrWhiteSpace(triggerReason) ? "manual" : triggerReason,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O")
        };
        dbContext.SavePoints.Add(savePoint);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new SavePointSummary(savePoint.SaveId, savePoint.EventId, savePoint.WorldEpoch, savePoint.TriggerReason, savePoint.CreatedAt, true);
    }

    public async Task<IReadOnlyList<SavePointSummary>> ListSavePointsAsync(CancellationToken cancellationToken = default)
    {
        await DatabaseInitializer.InitializeAsync(dbContext, cancellationToken);
        var latestId = await dbContext.SavePoints.MaxAsync(item => (int?)item.SaveId, cancellationToken) ?? 0;
        return await dbContext.SavePoints.AsNoTracking().OrderByDescending(item => item.SaveId)
            .Select(item => new SavePointSummary(item.SaveId, item.EventId, item.WorldEpoch, item.TriggerReason, item.CreatedAt, item.SaveId == latestId))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> DeleteSavePointAsync(int saveId, CancellationToken cancellationToken = default)
    {
        var savePoint = await dbContext.SavePoints.FindAsync([saveId], cancellationToken);
        if (savePoint is null)
        {
            return false;
        }

        dbContext.SavePoints.Remove(savePoint);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<DeathReturnResult> TriggerDeathReturnAsync(
        string deathCause,
        string? chronicleIndex = null,
        int? specificSaveId = null,
        IReadOnlyCollection<string>? memoryRetainers = null,
        CancellationToken cancellationToken = default)
    {
        await DatabaseInitializer.InitializeAsync(dbContext, cancellationToken);
        var savePoint = specificSaveId is int id
            ? await dbContext.SavePoints.FindAsync([id], cancellationToken)
            : await dbContext.SavePoints.OrderByDescending(item => item.SaveId).FirstOrDefaultAsync(cancellationToken);
        if (savePoint is null)
        {
            throw new InvalidOperationException("没有可用于死亡回归的存档。");
        }

        var session = await dbContext.ChatSessions.SingleAsync(item => item.IsActive == 1, cancellationToken);
        var previousMiasma = await dbContext.DeathReturnLog.OrderByDescending(item => item.LogId)
            .Select(item => (int?)item.MiasmaLevel).FirstOrDefaultAsync(cancellationToken) ?? 0;
        var loopCount = (await dbContext.DeathReturnLog.MaxAsync(item => (int?)item.LoopCount, cancellationToken) ?? 0) + 1;
        var miasmaResult = await diceEngine.ExecuteAsync($"瘴气 {previousMiasma}", cancellationToken);
        var newMiasma = ReadNewMiasma(miasmaResult);
        var newEpoch = session.CurrentWorldEpoch + 1;
        var rewindEventId = Guid.NewGuid().ToString("N");
        session.CurrentWorldEpoch = newEpoch;
        var retainers = memoryRetainers ?? [];
        var rewind = new WorldRewindRecord(savePoint.SaveId, "world_projection", retainers.ToList(), newEpoch, rewindEventId);
        var rewindEvent = await eventWriter.CommitAsync(
            session.SessionId,
            "WorldRewindCommitted",
            JsonSerializer.Serialize(rewind),
            eventId: rewindEventId,
            triggerCause: "death_return",
            cancellationToken: cancellationToken);

        await projectionReplayer.ReplayToSavePointAsync(savePoint, retainers, cancellationToken);

        dbContext.DeathReturnLog.Add(new DeathReturnLog
        {
            LoopCount = loopCount,
            DeathCause = string.IsNullOrWhiteSpace(deathCause) ? "未说明死因" : deathCause,
            MiasmaLevel = newMiasma,
            SavePointId = savePoint.SaveId,
            ChronicleIndex = chronicleIndex,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O")
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return new DeathReturnResult(savePoint.SaveId, loopCount, deathCause, newMiasma, rewindEvent.EventId);
    }

    private static int ReadNewMiasma(DiceResult result)
    {
        if (result.Tags.TryGetValue("新瘴气", out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var miasma))
        {
            return miasma;
        }
        throw new InvalidOperationException("瘴气检定结果缺少新瘴气值。");
    }
}

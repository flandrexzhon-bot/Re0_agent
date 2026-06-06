using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Dice;

namespace Re0Agent.Core.Services.Database;

public sealed record SavePointSummary(
    int SaveId,
    int Chapter,
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
    DiceEngine diceEngine)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task<SavePointSummary> CreateSavePointAsync(
        string triggerReason,
        CancellationToken cancellationToken = default)
    {
        await DatabaseInitializer.InitializeAsync(dbContext, cancellationToken);

        var globalState = await dbContext.GlobalStates.AsNoTracking()
            .OrderBy(state => state.RowId)
            .FirstOrDefaultAsync(cancellationToken);
        var protagonist = await dbContext.ProtagonistInfo.AsNoTracking()
            .OrderBy(item => item.RowId)
            .FirstOrDefaultAsync(cancellationToken);

        var savePoint = new SavePoint
        {
            Chapter = globalState?.CurrentChapter ?? 1,
            TriggerReason = string.IsNullOrWhiteSpace(triggerReason) ? "manual" : triggerReason,
            GlobalStateSnapshot = Serialize(globalState),
            ProtagonistSnapshot = Serialize(protagonist),
            WorldMapSnapshot = Serialize(await dbContext.WorldMapPoints.AsNoTracking().OrderBy(item => item.RowId).ToListAsync(cancellationToken)),
            MapElementsSnapshot = Serialize(await dbContext.MapElements.AsNoTracking().OrderBy(item => item.RowId).ToListAsync(cancellationToken)),
            FactionsSnapshot = Serialize(await dbContext.Factions.AsNoTracking().OrderBy(item => item.RowId).ToListAsync(cancellationToken)),
            NpcSnapshot = Serialize(await dbContext.ImportantNpcs.AsNoTracking().OrderBy(item => item.RowId).ToListAsync(cancellationToken)),
            InventorySnapshot = Serialize(await dbContext.Inventory.AsNoTracking().OrderBy(item => item.RowId).ToListAsync(cancellationToken)),
            EquipmentSnapshot = Serialize(await dbContext.Equipment.AsNoTracking().OrderBy(item => item.RowId).ToListAsync(cancellationToken)),
            QuestSnapshot = Serialize(await dbContext.Quests.AsNoTracking().OrderBy(item => item.RowId).ToListAsync(cancellationToken)),
            CreatedAt = await ReadCreatedAtAsync(cancellationToken)
        };

        dbContext.SavePoints.Add(savePoint);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new SavePointSummary(savePoint.SaveId, savePoint.Chapter, savePoint.TriggerReason, savePoint.CreatedAt, IsLatest: true);
    }

    public async Task<IReadOnlyList<SavePointSummary>> ListSavePointsAsync(
        CancellationToken cancellationToken = default)
    {
        await DatabaseInitializer.InitializeAsync(dbContext, cancellationToken);

        var latestId = await dbContext.SavePoints.AsNoTracking()
            .MaxAsync(item => (int?)item.SaveId, cancellationToken) ?? 0;

        return await dbContext.SavePoints.AsNoTracking()
            .OrderByDescending(item => item.SaveId)
            .Select(item => new SavePointSummary(
                item.SaveId,
                item.Chapter,
                item.TriggerReason,
                item.CreatedAt,
                item.SaveId == latestId))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> DeleteSavePointAsync(
        int saveId,
        CancellationToken cancellationToken = default)
    {
        await DatabaseInitializer.InitializeAsync(dbContext, cancellationToken);

        var latestId = await dbContext.SavePoints.AsNoTracking()
            .MaxAsync(item => (int?)item.SaveId, cancellationToken);
        if (latestId is null)
        {
            return false;
        }

        if (saveId == latestId.Value)
        {
            throw new InvalidOperationException("最新存档不可删除。");
        }

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
        CancellationToken cancellationToken = default)
    {
        await DatabaseInitializer.InitializeAsync(dbContext, cancellationToken);

        SavePoint? savePoint = null;
        if (specificSaveId.HasValue)
        {
            savePoint = await dbContext.SavePoints.FindAsync(new object[] { specificSaveId.Value }, cancellationToken);
        }
        else
        {
            savePoint = await dbContext.SavePoints.AsNoTracking()
                .OrderByDescending(item => item.SaveId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (savePoint is null)
        {
            throw new InvalidOperationException("没有可用于死亡回归的存档。");
        }

        var previousMiasma = await dbContext.DeathReturnLog.AsNoTracking()
            .OrderByDescending(item => item.LogId)
            .Select(item => (int?)item.MiasmaLevel)
            .FirstOrDefaultAsync(cancellationToken) ?? 0;
        var loopCount = (await dbContext.DeathReturnLog.AsNoTracking()
            .MaxAsync(item => (int?)item.LoopCount, cancellationToken) ?? 0) + 1;

        var miasmaResult = await diceEngine.ExecuteAsync($"瘴气 {previousMiasma}", cancellationToken);
        var newMiasma = ReadNewMiasma(miasmaResult);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var log = new DeathReturnLog
        {
            LoopCount = loopCount,
            DeathCause = string.IsNullOrWhiteSpace(deathCause) ? "未说明死因" : deathCause,
            MiasmaLevel = newMiasma,
            SavePointId = savePoint.SaveId,
            ChronicleIndex = chronicleIndex,
            CreatedAt = await ReadCreatedAtAsync(cancellationToken)
        };
        dbContext.DeathReturnLog.Add(log);
        await dbContext.SaveChangesAsync(cancellationToken);

        await RestoreGameStateAsync(savePoint, cancellationToken);
        await DeleteNonProtagonistMemoryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new DeathReturnResult(savePoint.SaveId, loopCount, log.DeathCause, newMiasma, miasmaResult.Detail ?? string.Empty);
    }

    private async Task RestoreGameStateAsync(
        SavePoint savePoint,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();

        await ReplaceSingleAsync(dbContext.GlobalStates, DeserializeSingle<GlobalState>(savePoint.GlobalStateSnapshot), cancellationToken);
        await ReplaceRowsAsync(dbContext.WorldMapPoints, DeserializeRows<WorldMapPoint>(savePoint.WorldMapSnapshot), cancellationToken);
        await ReplaceRowsAsync(dbContext.MapElements, DeserializeRows<MapElement>(savePoint.MapElementsSnapshot), cancellationToken);
        await ReplaceRowsAsync(dbContext.Factions, DeserializeRows<Faction>(savePoint.FactionsSnapshot), cancellationToken);
        await ReplaceSingleAsync(dbContext.ProtagonistInfo, DeserializeSingle<ProtagonistInfo>(savePoint.ProtagonistSnapshot), cancellationToken);
        await ReplaceRowsAsync(dbContext.ImportantNpcs, DeserializeRows<ImportantNpc>(savePoint.NpcSnapshot), cancellationToken);
        await ReplaceRowsAsync(dbContext.Inventory, DeserializeRows<InventoryItem>(savePoint.InventorySnapshot), cancellationToken);
        await ReplaceRowsAsync(dbContext.Equipment, DeserializeRows<EquipmentItem>(savePoint.EquipmentSnapshot), cancellationToken);
        await ReplaceRowsAsync(dbContext.Quests, DeserializeRows<Quest>(savePoint.QuestSnapshot), cancellationToken);
    }

    private async Task DeleteNonProtagonistMemoryAsync(CancellationToken cancellationToken)
    {
        var protagonistName = await dbContext.ProtagonistInfo.AsNoTracking()
            .Select(item => item.Name)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(protagonistName))
        {
            await dbContext.CharacterMemory.ExecuteDeleteAsync(cancellationToken);
            return;
        }

        await dbContext.CharacterMemory
            .Where(memory => memory.CharacterName != protagonistName)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task ReplaceSingleAsync<TEntity>(
        DbSet<TEntity> set,
        TEntity? entity,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        await set.ExecuteDeleteAsync(cancellationToken);
        if (entity is not null)
        {
            set.Add(entity);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
    }

    private async Task ReplaceRowsAsync<TEntity>(
        DbSet<TEntity> set,
        IReadOnlyCollection<TEntity> rows,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        await set.ExecuteDeleteAsync(cancellationToken);
        if (rows.Count > 0)
        {
            set.AddRange(rows);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
    }

    private async Task<string> ReadCreatedAtAsync(CancellationToken cancellationToken)
    {
        var gameTime = await dbContext.GlobalStates.AsNoTracking()
            .Select(item => item.CurTime)
            .FirstOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(gameTime)
            ? DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            : gameTime;
    }

    private static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static T? DeserializeSingle<T>(string value)
        where T : class
    {
        return JsonSerializer.Deserialize<T?>(value, JsonOptions);
    }

    private static IReadOnlyCollection<T> DeserializeRows<T>(string value)
        where T : class
    {
        return JsonSerializer.Deserialize<List<T>>(value, JsonOptions) ?? [];
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

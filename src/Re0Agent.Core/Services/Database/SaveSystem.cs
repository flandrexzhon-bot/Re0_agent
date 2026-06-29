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
        var savePoint = await BuildSnapshotAsync(triggerReason, cancellationToken);
        dbContext.SavePoints.Add(savePoint);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new SavePointSummary(savePoint.SaveId, savePoint.Chapter, savePoint.TriggerReason, savePoint.CreatedAt, IsLatest: true);
    }

    /// <summary>
    /// 抓取当前 9 张表的快照，返回未持久化的 <see cref="SavePoint"/> 实例（不写库）。
    /// 供 <see cref="CreateSavePointAsync"/> 与内存重 roll cache 复用。
    /// </summary>
    public async Task<SavePoint> CaptureInMemorySnapshotAsync(CancellationToken cancellationToken = default)
        => await BuildSnapshotAsync("inmemory_reroll", cancellationToken);

    private async Task<SavePoint> BuildSnapshotAsync(
        string triggerReason,
        CancellationToken cancellationToken)
    {
        await DatabaseInitializer.InitializeAsync(dbContext, cancellationToken);

        var globalState = await dbContext.GlobalStates.AsNoTracking()
            .OrderBy(state => state.RowId)
            .FirstOrDefaultAsync(cancellationToken);
        var protagonist = await dbContext.ProtagonistInfo.AsNoTracking()
            .OrderBy(item => item.RowId)
            .FirstOrDefaultAsync(cancellationToken);

        return new SavePoint
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
            // chronicle / character_memory 也快照：fork 与重 roll restore 时按平行时间线精确回滚，
            // 这样新回合编号（=Chronicle.Count()+1）回到平行 R{N} 而非顺延，且不依赖任何计数启发式。
            // 死亡回归不回滚这两张表（见 RestoreGameStateAsync 的 restoreChronicleAndMemory 开关）。
            ChronicleSnapshot = Serialize(await dbContext.Chronicle.AsNoTracking().OrderBy(item => item.RowId).ToListAsync(cancellationToken)),
            CharacterMemorySnapshot = Serialize(await dbContext.CharacterMemory.AsNoTracking().OrderBy(item => item.RowId).ToListAsync(cancellationToken)),
            CreatedAt = await ReadCreatedAtAsync(cancellationToken)
        };
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

        // 死亡回归：只回滚 9 张状态表，chronicle（append-only 元历史）与非主角记忆另行处理 —— 不回滚 chronicle。
        await RestoreGameStateAsync(savePoint, restoreChronicleAndMemory: false, cancellationToken);
        await DeleteNonProtagonistMemoryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new DeathReturnResult(savePoint.SaveId, loopCount, log.DeathCause, newMiasma, miasmaResult.Detail ?? string.Empty);
    }

    /// <summary>
    /// fork「引入叙事」：从指定存档锚点 restore 世界状态（复用死亡回归的 restore 路径），
    /// 但<strong>不</strong>写 DeathReturnLog、<strong>不</strong>滚瘴气、<strong>不</strong>计入循环次数。
    /// 用于「类死亡回归但不计死亡」的分歧点回溯。
    /// <para>新存档锚点含 chronicle/character_memory 快照时直接精确回滚（平行时间线就位，
    /// 新回合编号自然回到平行 R{N}）；旧存档无快照时降级到按 <paramref name="forkRoundIndex"/>
    /// 截断 chronicle/记忆的启发式。</para>
    /// </summary>
    public async Task ForkRestoreAsync(int saveId, string forkRoundIndex, CancellationToken cancellationToken = default)
    {
        await DatabaseInitializer.InitializeAsync(dbContext, cancellationToken);

        var savePoint = await dbContext.SavePoints.FindAsync(new object[] { saveId }, cancellationToken)
            ?? throw new InvalidOperationException($"找不到用于 fork 的存档锚点 #{saveId}。");

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        if (savePoint.ChronicleSnapshot is not null)
        {
            // 新存档：chronicle/记忆随快照精确回滚到锚点时刻，平行时间线就位。
            await RestoreGameStateAsync(savePoint, restoreChronicleAndMemory: true, cancellationToken);
            await DeleteNonProtagonistMemoryAsync(cancellationToken);
        }
        else
        {
            // 旧存档（fork 列加列前创建）：无 chronicle 快照，沿用按回合号截断的启发式兜底。
            await RestoreGameStateAsync(savePoint, restoreChronicleAndMemory: false, cancellationToken);
            await DeleteNonProtagonistMemoryAsync(cancellationToken);
            await TrimChronicleForForkAsync(forkRoundIndex, cancellationToken);
            await DeleteMemoryFromRoundAsync(forkRoundIndex, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// 把 chronicle 行数恢复到 fork 点：fork 到 R{N} 的平行回合，须保留前 N-1 条 AM，删掉其后全部
    /// （即被 fork 回合及更晚回合写入的总结）。直接逆推 CreateRoundIndexAsync 的 R{Count+1} 公式，
    /// 与 AM0000 序章是否存在无关。
    /// </summary>
    private async Task TrimChronicleForForkAsync(string forkRoundIndex, CancellationToken cancellationToken)
    {
        if (!TryParseRoundNumber(forkRoundIndex, out var roundNumber))
        {
            return;
        }

        var keepCount = Math.Max(0, roundNumber - 1);
        var ordered = await dbContext.Chronicle
            .OrderBy(c => c.RowId)
            .ToListAsync(cancellationToken);

        var toDelete = ordered.Skip(keepCount).ToList();
        if (toDelete.Count > 0)
        {
            dbContext.Chronicle.RemoveRange(toDelete);
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
        }
    }

    /// <summary>删除被 fork 回合及更晚回合（round_index &gt;= forkRoundIndex，零填充故可字典序比较）写入的角色记忆。</summary>
    private async Task DeleteMemoryFromRoundAsync(string forkRoundIndex, CancellationToken cancellationToken)
    {
        if (!TryParseRoundNumber(forkRoundIndex, out var forkNumber))
        {
            return;
        }

        var stale = await dbContext.CharacterMemory
            .Where(m => m.RoundIndex.StartsWith("R"))
            .ToListAsync(cancellationToken);

        var toDelete = stale
            .Where(m => TryParseRoundNumber(m.RoundIndex, out var n) && n >= forkNumber)
            .ToList();

        if (toDelete.Count > 0)
        {
            dbContext.CharacterMemory.RemoveRange(toDelete);
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
        }
    }

    private static bool TryParseRoundNumber(string? roundIndex, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(roundIndex))
        {
            return false;
        }

        var digits = roundIndex.TrimStart('R', 'r');
        return int.TryParse(digits, out number);
    }

    /// <summary>
    /// 用给定快照（可来自持久化存档或内存重 roll cache）覆盖 9 张状态表。
    /// </summary>
    public async Task RestoreGameStateAsync(
        SavePoint savePoint,
        CancellationToken cancellationToken)
        => await RestoreGameStateAsync(savePoint, restoreChronicleAndMemory: true, cancellationToken);

    /// <summary>
    /// 用给定快照覆盖状态表。<paramref name="restoreChronicleAndMemory"/> 为 true 时（fork / 重 roll /
    /// 变体切换）额外按快照精确回滚 chronicle 与 character_memory，构成平行时间线；为 false 时
    /// （死亡回归）保留 chronicle（append-only 元历史）与记忆，交由调用方另行处理。
    /// 快照不含这两张表的数据（旧存档）时即便开关为 true 也跳过，避免误清空。
    /// </summary>
    public async Task RestoreGameStateAsync(
        SavePoint savePoint,
        bool restoreChronicleAndMemory,
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

        if (restoreChronicleAndMemory && savePoint.ChronicleSnapshot is not null)
        {
            await ReplaceRowsAsync(dbContext.Chronicle, DeserializeRows<ChronicleEntry>(savePoint.ChronicleSnapshot), cancellationToken);
            await ReplaceRowsAsync(dbContext.CharacterMemory, DeserializeRows<CharacterMemory>(savePoint.CharacterMemorySnapshot ?? "[]"), cancellationToken);
        }
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

using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;

namespace Re0Agent.Core.Services.Database;

public sealed class ChatSessionService(
    Re0AgentDbContext dbContext,
    ProtagonistTemplateService templateService)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task EnsureDefaultSessionAsync(CancellationToken cancellationToken = default)
    {
        await DatabaseInitializer.InitializeAsync(dbContext, cancellationToken);

        var anySession = await dbContext.ChatSessions.AnyAsync(cancellationToken);
        if (anySession)
        {
            return;
        }

        // Initialize sandbox with default Subaru template
        await templateService.EnsureDefaultTemplateAsync(cancellationToken);
        var defaultTemplate = await dbContext.ProtagonistTemplates
            .FirstOrDefaultAsync(t => t.IsDefault == 1, cancellationToken);
        
        if (defaultTemplate is not null)
        {
            await ClearSandboxTablesAsync(cancellationToken);
            await templateService.ApplyTemplateAsync(defaultTemplate.TemplateId, cancellationToken);
        }

        // Create the default session
        var defaultSession = new ChatSession
        {
            SessionName = "默认会话",
            IsActive = 1,
            CreatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm"),
            DetailedRoundsSnapshot = "[]"
        };

        // Take snapshot of current state
        await CaptureSnapshotsAsync(defaultSession, cancellationToken);

        dbContext.ChatSessions.Add(defaultSession);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChatSession>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDefaultSessionAsync(cancellationToken);
        return await dbContext.ChatSessions
            .OrderByDescending(s => s.SessionId)
            .ToListAsync(cancellationToken);
    }

    public async Task<ChatSession?> GetActiveSessionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureDefaultSessionAsync(cancellationToken);
        return await dbContext.ChatSessions
            .FirstOrDefaultAsync(s => s.IsActive == 1, cancellationToken);
    }

    public async Task SaveActiveSessionStateAsync(string detailedRoundsJson, CancellationToken cancellationToken = default)
        => await SaveActiveSessionStateAsync(detailedRoundsJson, roundVariantsJson: null, cancellationToken);

    /// <summary>
    /// 落盘当前活动会话：回合明细 + 13 表快照，并可选地带上各回合重 roll 变体集合
    /// （<paramref name="roundVariantsJson"/> 为 null 时保留库中原值不动）。
    /// </summary>
    public async Task SaveActiveSessionStateAsync(string detailedRoundsJson, string? roundVariantsJson, CancellationToken cancellationToken = default)
    {
        var activeSession = await GetActiveSessionAsync(cancellationToken);
        if (activeSession is null)
        {
            return;
        }

        activeSession.DetailedRoundsSnapshot = detailedRoundsJson;
        if (roundVariantsJson is not null)
        {
            activeSession.RoundVariantsSnapshot = roundVariantsJson;
        }
        await CaptureSnapshotsAsync(activeSession, cancellationToken);

        dbContext.Entry(activeSession).State = EntityState.Modified;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<(string DetailedRoundsJson, string RoundVariantsJson)> SwitchSessionAsync(int targetSessionId, string currentDetailedRoundsJson, string? currentRoundVariantsJson, CancellationToken cancellationToken = default)
    {
        // 1. Save current active session
        await SaveActiveSessionStateAsync(currentDetailedRoundsJson, currentRoundVariantsJson, cancellationToken);

        // 2. Perform switch
        var transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            var targetSession = await dbContext.ChatSessions
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.SessionId == targetSessionId, cancellationToken);
            
            if (targetSession is null)
            {
                throw new InvalidOperationException("找不到目标会话。");
            }

            // Restore target session's sandbox tables
            await ClearSandboxTablesAsync(cancellationToken);
            await RestoreSandboxSnapshotsAsync(targetSession, cancellationToken);

            // Now that sandbox is restored and ChangeTracker is cleared,
            // load, modify, and save the session active states
            var sessions = await dbContext.ChatSessions.ToListAsync(cancellationToken);
            foreach (var s in sessions)
            {
                s.IsActive = s.SessionId == targetSessionId ? 1 : 0;
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return (targetSession.DetailedRoundsSnapshot, targetSession.RoundVariantsSnapshot);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            throw;
        }
    }

    public async Task<string> CreateNewSessionAsync(string sessionName, CancellationToken cancellationToken = default)
    {
        // 1. Save current active session (with empty/current rounds snapshot)
        var activeSession = await GetActiveSessionAsync(cancellationToken);
        string currentRounds = activeSession?.DetailedRoundsSnapshot ?? "[]";
        await SaveActiveSessionStateAsync(currentRounds, cancellationToken);

        // 2. Create new session state
        var transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            // Clear database sandbox
            await ClearSandboxTablesAsync(cancellationToken);

            // Apply default Subaru template
            await templateService.EnsureDefaultTemplateAsync(cancellationToken);
            var defaultTemplate = await dbContext.ProtagonistTemplates
                .FirstOrDefaultAsync(t => t.IsDefault == 1, cancellationToken);
            
            if (defaultTemplate is not null)
            {
                await templateService.ApplyTemplateAsync(defaultTemplate.TemplateId, cancellationToken);
            }

            // Now load sessions, deactivate, and add the new session
            var sessions = await dbContext.ChatSessions.ToListAsync(cancellationToken);
            foreach (var s in sessions)
            {
                s.IsActive = 0;
            }

            // Create new ChatSession
            var newSession = new ChatSession
            {
                SessionName = string.IsNullOrWhiteSpace(sessionName) ? "未命名会话" : sessionName,
                IsActive = 1,
                CreatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm"),
                DetailedRoundsSnapshot = "[]"
            };

            await CaptureSnapshotsAsync(newSession, cancellationToken);
            dbContext.ChatSessions.Add(newSession);

            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return "[]";
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            throw;
        }
    }

    /// <summary>
    /// SillyTavern 式 branch：把源会话克隆为一个新会话，回合历史与世界快照截断到 fork 回合
    /// （含该回合）。<strong>不</strong>触碰当前 live sandbox、<strong>不</strong>改源会话——纯快照→快照拼装。
    /// </summary>
    /// <param name="sourceSessionId">分支来源会话。</param>
    /// <param name="truncatedRoundsJson">已截断到 fork 回合（含）的 DetailedRounds JSON。</param>
    /// <param name="endSavePointId">fork 回合结束时的存档 ID（决定世界状态截断点）；null 则整盘复制源快照。</param>
    /// <param name="newName">新分支会话名。</param>
    /// <returns>新会话 SessionId。</returns>
    public async Task<int> BranchSessionAsync(
        int sourceSessionId,
        string truncatedRoundsJson,
        int? endSavePointId,
        string newName,
        IReadOnlyCollection<string>? keptRoundIndices,
        CancellationToken cancellationToken = default)
    {
        await EnsureDefaultSessionAsync(cancellationToken);

        var source = await dbContext.ChatSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SessionId == sourceSessionId, cancellationToken)
            ?? throw new InvalidOperationException("找不到分支来源会话。");

        var branch = new ChatSession
        {
            SessionName = string.IsNullOrWhiteSpace(newName) ? "未命名分支" : newName,
            IsActive = 0,
            ParentSessionId = sourceSessionId,
            CreatedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm"),
            DetailedRoundsSnapshot = string.IsNullOrWhiteSpace(truncatedRoundsJson) ? "[]" : truncatedRoundsJson,
            // death_return_log 原样复制（循环纪事是跨分支的元历史）。
            DeathReturnLogSnapshot = source.DeathReturnLogSnapshot,
            // 重 roll 变体：仅复制保留回合（RoundIndex ≤ fork 回合）的变体集合，
            // 使分支保留这些回合的历史 roll；fork 回合成为分支最新回合后其变体自动重现。
            RoundVariantsSnapshot = FilterRoundVariants(source.RoundVariantsSnapshot, keptRoundIndices)
        };

        // 选定 fork 回合结束时的存档：决定 9 表 + chronicle + 记忆的截断状态。
        var savePoints = DeserializeList<SavePoint>(source.SavePointsSnapshot);
        var anchor = endSavePointId is int endId
            ? savePoints.FirstOrDefault(sp => sp.SaveId == endId)
            : savePoints.OrderByDescending(sp => sp.SaveId).FirstOrDefault();

        if (anchor is null)
        {
            // 无可用存档锚点：整盘复制源会话快照兜底（至少世界状态与源一致，回合已截断）。
            branch.GlobalStateSnapshot = source.GlobalStateSnapshot;
            branch.ProtagonistSnapshot = source.ProtagonistSnapshot;
            branch.WorldMapSnapshot = source.WorldMapSnapshot;
            branch.MapElementsSnapshot = source.MapElementsSnapshot;
            branch.FactionsSnapshot = source.FactionsSnapshot;
            branch.NpcSnapshot = source.NpcSnapshot;
            branch.InventorySnapshot = source.InventorySnapshot;
            branch.EquipmentSnapshot = source.EquipmentSnapshot;
            branch.QuestSnapshot = source.QuestSnapshot;
            branch.ChronicleSnapshot = source.ChronicleSnapshot;
            branch.CharacterMemorySnapshot = source.CharacterMemorySnapshot;
            branch.SavePointsSnapshot = source.SavePointsSnapshot;
        }
        else
        {
            // 用锚点存档覆盖各表快照（存档本身就是 9 表 + chronicle + 记忆的整盘快照）。
            branch.GlobalStateSnapshot = anchor.GlobalStateSnapshot;
            branch.ProtagonistSnapshot = anchor.ProtagonistSnapshot;
            branch.WorldMapSnapshot = anchor.WorldMapSnapshot;
            branch.MapElementsSnapshot = anchor.MapElementsSnapshot;
            branch.FactionsSnapshot = anchor.FactionsSnapshot;
            branch.NpcSnapshot = anchor.NpcSnapshot;
            branch.InventorySnapshot = anchor.InventorySnapshot;
            branch.EquipmentSnapshot = anchor.EquipmentSnapshot;
            branch.QuestSnapshot = anchor.QuestSnapshot;
            // 锚点存档的 chronicle/memory 快照在加列前可能为 null，回退到源会话快照。
            branch.ChronicleSnapshot = anchor.ChronicleSnapshot ?? source.ChronicleSnapshot;
            branch.CharacterMemorySnapshot = anchor.CharacterMemorySnapshot ?? source.CharacterMemorySnapshot;
            // save_points 截断到锚点（含）为止——分支不该看到 fork 点之后的存档。
            var keptSavePoints = savePoints.Where(sp => sp.SaveId <= anchor.SaveId).ToList();
            branch.SavePointsSnapshot = JsonSerializer.Serialize(keptSavePoints, JsonOptions);
        }

        dbContext.ChatSessions.Add(branch);
        await dbContext.SaveChangesAsync(cancellationToken);
        return branch.SessionId;
    }

    private static List<T> DeserializeList<T>(string json)
        => string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? [];

    /// <summary>
    /// 从源会话的「各回合变体」JSON（Dictionary&lt;RoundIndex, RoundVariantSet&gt;）里，
    /// 只保留 <paramref name="keptRoundIndices"/> 指定的回合条目。null 表示全部保留。
    /// 解析失败或为空时回退到 "{}"。
    /// </summary>
    private static string FilterRoundVariants(string sourceJson, IReadOnlyCollection<string>? keptRoundIndices)
    {
        if (string.IsNullOrWhiteSpace(sourceJson) || sourceJson == "{}")
        {
            return "{}";
        }
        if (keptRoundIndices is null)
        {
            return sourceJson;
        }

        try
        {
            var all = JsonSerializer.Deserialize<Dictionary<string, RoundVariantSet>>(sourceJson, JsonOptions);
            if (all is null || all.Count == 0)
            {
                return "{}";
            }
            var keep = new HashSet<string>(keptRoundIndices, StringComparer.Ordinal);
            var filtered = all.Where(kvp => keep.Contains(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            return JsonSerializer.Serialize(filtered, JsonOptions);
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    public async Task DeleteSessionAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        var targetSession = await dbContext.ChatSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SessionId == sessionId, cancellationToken);
        if (targetSession is null)
        {
            return;
        }

        var totalSessionsCount = await dbContext.ChatSessions.CountAsync(cancellationToken);
        if (totalSessionsCount <= 1)
        {
            throw new InvalidOperationException("无法删除唯一的聊天记录。");
        }

        var transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            if (targetSession.IsActive == 1)
            {
                // Find another session to switch to
                var otherSession = await dbContext.ChatSessions
                    .AsNoTracking()
                    .Where(s => s.SessionId != sessionId)
                    .OrderByDescending(s => s.SessionId)
                    .FirstAsync(cancellationToken);

                // Clear and restore first
                await ClearSandboxTablesAsync(cancellationToken);
                await RestoreSandboxSnapshotsAsync(otherSession, cancellationToken);

                // Now load and mark otherSession active, and save
                var sessions = await dbContext.ChatSessions.ToListAsync(cancellationToken);
                foreach (var s in sessions)
                {
                    s.IsActive = s.SessionId == otherSession.SessionId ? 1 : 0;
                }
            }

            // Remove target
            var toRemove = await dbContext.ChatSessions.FirstOrDefaultAsync(s => s.SessionId == sessionId, cancellationToken);
            if (toRemove is not null)
            {
                dbContext.ChatSessions.Remove(toRemove);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            throw;
        }
    }

    private async Task ClearSandboxTablesAsync(CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await dbContext.GlobalStates.ExecuteDeleteAsync(cancellationToken);
        await dbContext.WorldMapPoints.ExecuteDeleteAsync(cancellationToken);
        await dbContext.MapElements.ExecuteDeleteAsync(cancellationToken);
        await dbContext.Factions.ExecuteDeleteAsync(cancellationToken);
        await dbContext.ProtagonistInfo.ExecuteDeleteAsync(cancellationToken);
        await dbContext.ImportantNpcs.ExecuteDeleteAsync(cancellationToken);
        await dbContext.Inventory.ExecuteDeleteAsync(cancellationToken);
        await dbContext.Equipment.ExecuteDeleteAsync(cancellationToken);
        await dbContext.Quests.ExecuteDeleteAsync(cancellationToken);
        await dbContext.Chronicle.ExecuteDeleteAsync(cancellationToken);
        await dbContext.CharacterMemory.ExecuteDeleteAsync(cancellationToken);
        await dbContext.DeathReturnLog.ExecuteDeleteAsync(cancellationToken);
        await dbContext.SavePoints.ExecuteDeleteAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
    }

    private async Task CaptureSnapshotsAsync(ChatSession session, CancellationToken cancellationToken)
    {
        var globalState = await dbContext.GlobalStates.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var worldMap = await dbContext.WorldMapPoints.AsNoTracking().ToListAsync(cancellationToken);
        var mapElements = await dbContext.MapElements.AsNoTracking().ToListAsync(cancellationToken);
        var factions = await dbContext.Factions.AsNoTracking().ToListAsync(cancellationToken);
        var npcs = await dbContext.ImportantNpcs.AsNoTracking().ToListAsync(cancellationToken);
        var inventory = await dbContext.Inventory.AsNoTracking().ToListAsync(cancellationToken);
        var equipment = await dbContext.Equipment.AsNoTracking().ToListAsync(cancellationToken);
        var quests = await dbContext.Quests.AsNoTracking().ToListAsync(cancellationToken);
        var chronicle = await dbContext.Chronicle.AsNoTracking().ToListAsync(cancellationToken);
        var memory = await dbContext.CharacterMemory.AsNoTracking().ToListAsync(cancellationToken);
        var deathReturn = await dbContext.DeathReturnLog.AsNoTracking().ToListAsync(cancellationToken);
        var savePoints = await dbContext.SavePoints.AsNoTracking().ToListAsync(cancellationToken);

        session.GlobalStateSnapshot = JsonSerializer.Serialize(globalState, JsonOptions);
        session.ProtagonistSnapshot = JsonSerializer.Serialize(protagonist, JsonOptions);
        session.WorldMapSnapshot = JsonSerializer.Serialize(worldMap, JsonOptions);
        session.MapElementsSnapshot = JsonSerializer.Serialize(mapElements, JsonOptions);
        session.FactionsSnapshot = JsonSerializer.Serialize(factions, JsonOptions);
        session.NpcSnapshot = JsonSerializer.Serialize(npcs, JsonOptions);
        session.InventorySnapshot = JsonSerializer.Serialize(inventory, JsonOptions);
        session.EquipmentSnapshot = JsonSerializer.Serialize(equipment, JsonOptions);
        session.QuestSnapshot = JsonSerializer.Serialize(quests, JsonOptions);
        session.ChronicleSnapshot = JsonSerializer.Serialize(chronicle, JsonOptions);
        session.CharacterMemorySnapshot = JsonSerializer.Serialize(memory, JsonOptions);
        session.DeathReturnLogSnapshot = JsonSerializer.Serialize(deathReturn, JsonOptions);
        session.SavePointsSnapshot = JsonSerializer.Serialize(savePoints, JsonOptions);
    }

    private async Task RestoreSandboxSnapshotsAsync(ChatSession session, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();

        var globalState = JsonSerializer.Deserialize<GlobalState>(session.GlobalStateSnapshot, JsonOptions);
        if (globalState is not null) dbContext.GlobalStates.Add(globalState);

        var protagonist = JsonSerializer.Deserialize<ProtagonistInfo>(session.ProtagonistSnapshot, JsonOptions);
        if (protagonist is not null) dbContext.ProtagonistInfo.Add(protagonist);

        var worldMap = JsonSerializer.Deserialize<List<WorldMapPoint>>(session.WorldMapSnapshot, JsonOptions);
        if (worldMap is not null) dbContext.WorldMapPoints.AddRange(worldMap);

        var mapElements = JsonSerializer.Deserialize<List<MapElement>>(session.MapElementsSnapshot, JsonOptions);
        if (mapElements is not null) dbContext.MapElements.AddRange(mapElements);

        var factions = JsonSerializer.Deserialize<List<Faction>>(session.FactionsSnapshot, JsonOptions);
        if (factions is not null) dbContext.Factions.AddRange(factions);

        var npcs = JsonSerializer.Deserialize<List<ImportantNpc>>(session.NpcSnapshot, JsonOptions);
        if (npcs is not null) dbContext.ImportantNpcs.AddRange(npcs);

        var inventory = JsonSerializer.Deserialize<List<InventoryItem>>(session.InventorySnapshot, JsonOptions);
        if (inventory is not null) dbContext.Inventory.AddRange(inventory);

        var equipment = JsonSerializer.Deserialize<List<EquipmentItem>>(session.EquipmentSnapshot, JsonOptions);
        if (equipment is not null) dbContext.Equipment.AddRange(equipment);

        var quests = JsonSerializer.Deserialize<List<Quest>>(session.QuestSnapshot, JsonOptions);
        if (quests is not null) dbContext.Quests.AddRange(quests);

        var chronicle = JsonSerializer.Deserialize<List<ChronicleEntry>>(session.ChronicleSnapshot, JsonOptions);
        if (chronicle is not null) dbContext.Chronicle.AddRange(chronicle);

        var memory = JsonSerializer.Deserialize<List<CharacterMemory>>(session.CharacterMemorySnapshot, JsonOptions);
        if (memory is not null) dbContext.CharacterMemory.AddRange(memory);

        var deathReturn = JsonSerializer.Deserialize<List<DeathReturnLog>>(session.DeathReturnLogSnapshot, JsonOptions);
        if (deathReturn is not null) dbContext.DeathReturnLog.AddRange(deathReturn);

        var savePoints = JsonSerializer.Deserialize<List<SavePoint>>(session.SavePointsSnapshot, JsonOptions);
        if (savePoints is not null) dbContext.SavePoints.AddRange(savePoints);

        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
    }
}

using System.Text;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;

namespace Re0Agent.Core.Services.Agent;

/// <summary>
/// 数据库摘要构建器，提供两种视角：
///  - <see cref="BuildFullAsync"/>：全量数据库摘要（所有 SQL 表）。GM(开普勒)、角色调度(泉此方)、
///    章节切换(帕秋莉) 共用，让它们纵览整个现世处境。
///  - <see cref="BuildForCharacterAsync"/>：角色 Agent 专用的【受限视角】——只含全局状态栏、世界地图点、
///    地图元素，以及该角色自己的那一栏（NPC 看 important_npc，主角看 protagonist_info）。
/// </summary>
public static class DbSummaryBuilder
{
    /// <summary>
    /// 兼容旧调用：等同全量摘要。历史上此方法只给「全局+主角+在册NPC」，现统一升级为全量，
    /// 让所有非角色 Agent 都能看到整个数据库。
    /// </summary>
    public static Task<string> BuildAsync(Re0AgentDbContext dbContext, CancellationToken cancellationToken)
        => BuildFullAsync(dbContext, cancellationToken);

    /// <summary>全量数据库摘要：逐表渲染所有业务表，供 GM / 泉此方 / 帕秋莉 等非角色 Agent 使用。</summary>
    public static async Task<string> BuildFullAsync(Re0AgentDbContext dbContext, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();

        var state = await dbContext.GlobalStates.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        sb.Append("[全局状态栏] ").AppendLine(FormatGlobalState(state));

        var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        sb.Append("[主角] ").AppendLine(FormatProtagonist(protagonist));

        var npcs = await dbContext.ImportantNpcs.AsNoTracking().OrderBy(n => n.RowId).ToListAsync(cancellationToken);
        sb.Append("[在册NPC] ").AppendLine(npcs.Count == 0
            ? "（无）"
            : string.Join("；", npcs.Select(FormatNpc)));

        var mapPoints = await dbContext.WorldMapPoints.AsNoTracking().OrderBy(p => p.RowId).ToListAsync(cancellationToken);
        sb.Append("[世界地图点] ").AppendLine(FormatMapPoints(mapPoints));

        var mapElements = await dbContext.MapElements.AsNoTracking().OrderBy(e => e.RowId).ToListAsync(cancellationToken);
        sb.Append("[地图元素] ").AppendLine(FormatMapElements(mapElements));

        var factions = await dbContext.Factions.AsNoTracking().OrderBy(f => f.RowId).ToListAsync(cancellationToken);
        sb.Append("[势力] ").AppendLine(factions.Count == 0
            ? "（无）"
            : string.Join("；", factions.Select(f => $"{f.FactionName}（领袖:{f.Leader ?? "?"}，{f.Description}）")));

        var inventory = await dbContext.Inventory.AsNoTracking().OrderBy(i => i.RowId).ToListAsync(cancellationToken);
        sb.Append("[物品] ").AppendLine(inventory.Count == 0
            ? "（无）"
            : string.Join("；", inventory.Select(i => $"{i.ItemName}x{i.Quantity}（{i.Quality}）")));

        var equipment = await dbContext.Equipment.AsNoTracking().OrderBy(e => e.RowId).ToListAsync(cancellationToken);
        sb.Append("[装备] ").AppendLine(equipment.Count == 0
            ? "（无）"
            : string.Join("；", equipment.Select(e => $"{e.EquipmentName}（{e.StatusText}）")));

        var quests = await dbContext.Quests.AsNoTracking().OrderBy(q => q.RowId).ToListAsync(cancellationToken);
        sb.Append("[任务] ").AppendLine(quests.Count == 0
            ? "（无）"
            : string.Join("；", quests.Select(q => $"{q.QuestName}[{q.QuestType}/{q.StatusTag}/{q.ProgressText}]")));

        var chronicleCount = await dbContext.Chronicle.CountAsync(cancellationToken);
        var lastChronicle = await dbContext.Chronicle.AsNoTracking()
            .OrderByDescending(c => c.RowId)
            .Select(c => $"{c.CodeIndex}：{c.Summary}")
            .FirstOrDefaultAsync(cancellationToken);
        sb.Append("[编年史] ").Append($"共{chronicleCount}条")
            .AppendLine(lastChronicle is null ? "" : $"，最新：{lastChronicle}");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 角色 Agent 受限视角：只给全局状态栏、世界地图点、地图元素，以及该角色自己的那一栏。
    /// </summary>
    /// <param name="characterName">角色全名/规范名。</param>
    /// <param name="isPlayerControlled">是否主角（主角看 protagonist_info，NPC 看 important_npc）。</param>
    public static async Task<string> BuildForCharacterAsync(
        Re0AgentDbContext dbContext,
        string characterName,
        bool isPlayerControlled,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        string? actorLocation;

        if (isPlayerControlled)
        {
            var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
            sb.Append("[我的状态（主角）] ").AppendLine(FormatProtagonist(protagonist));
            actorLocation = protagonist?.LocationName;
        }
        else
        {
            var npc = await dbContext.ImportantNpcs.AsNoTracking()
                .FirstOrDefaultAsync(n => n.Name == characterName, cancellationToken);
            sb.Append($"[我的状态（{characterName}）] ").AppendLine(npc is null ? "（我尚未在册）" : FormatNpcDetailed(npc));
            actorLocation = npc?.LocationName;
        }

        var state = await dbContext.GlobalStates.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        sb.Append("[当前时间] ").AppendLine(state is null ? "无" : $"{state.CurTime}，章节:{state.CurrentChapter}");
        var mapPoints = await dbContext.WorldMapPoints.AsNoTracking()
            .Where(point => point.LocationName == actorLocation || point.ExplorationStatus != "未探索")
            .OrderBy(point => point.RowId).ToListAsync(cancellationToken);
        sb.Append("[已知地图点] ").AppendLine(FormatMapPoints(mapPoints));
        var mapElements = string.IsNullOrWhiteSpace(actorLocation) ? [] : await dbContext.MapElements.AsNoTracking()
            .Where(element => element.LocationName == actorLocation).OrderBy(element => element.RowId).ToListAsync(cancellationToken);
        sb.Append("[当前场景元素] ").AppendLine(FormatMapElements(mapElements));

        return sb.ToString().TrimEnd();
    }

    private static string FormatGlobalState(GlobalState? state) => state is null
        ? "无"
        : $"位置:{state.CurrentLocation}/{state.CurrentMinorRegion}/{state.CurrentMajorRegion}，时间:{state.CurTime}，章节:{state.CurrentChapter}";

    private static string FormatProtagonist(ProtagonistInfo? p) => p is null
        ? "无"
        : $"{p.Name}，位于{p.LocationName}，状态:{p.SelfStatus}，{p.BaseAttributes}{(string.IsNullOrWhiteSpace(p.SpecialAttributes) ? "" : "，" + p.SpecialAttributes)}";

    private static string FormatNpc(ImportantNpc n)
        => $"{n.Name}（{n.LocationName}，{n.SelfStatus}，{n.BaseAttributes}）";

    private static string FormatNpcDetailed(ImportantNpc n)
        => $"{n.Name}，{n.Gender}/{n.Age}岁，位于{n.LocationName}，状态:{n.SelfStatus}，{n.BaseAttributes}"
        + $"{(string.IsNullOrWhiteSpace(n.SpecialAttributes) ? "" : "，" + n.SpecialAttributes)}"
        + $"{(string.IsNullOrWhiteSpace(n.RelationsText) ? "" : "，关系:" + n.RelationsText)}";

    private static string FormatMapPoints(IReadOnlyList<WorldMapPoint> points) => points.Count == 0
        ? "（无）"
        : string.Join("；", points.Select(p => $"{p.LocationName}（{p.MinorRegion}/{p.MajorRegion}，{p.LocationType}，{p.ExplorationStatus}）"));

    private static string FormatMapElements(IReadOnlyList<MapElement> elements) => elements.Count == 0
        ? "（无）"
        : string.Join("；", elements.Select(e => $"{e.ElementName}[{e.ElementType}]@{e.LocationName}（{e.StatusText}）"));
}

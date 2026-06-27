using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;

namespace Re0Agent.Core.Services.Agent;

/// <summary>
/// 构建「全局状态 + 主角 + 在册NPC」数据库摘要。GM 开普勒与角色调度员泉此方共用，
/// 确保两者拿到完全一致的现世处境上下文。
/// </summary>
public static class DbSummaryBuilder
{
    public static async Task<string> BuildAsync(Re0AgentDbContext dbContext, CancellationToken cancellationToken)
    {
        var state = await dbContext.GlobalStates.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var npcs = await dbContext.ImportantNpcs.AsNoTracking()
            .Select(n => $"{n.Name}（{n.LocationName}，{n.SelfStatus}，{n.BaseAttributes}）")
            .ToListAsync(cancellationToken);

        var stateStr = state is null ? "无" :
            $"位置:{state.CurrentLocation}/{state.CurrentMinorRegion}/{state.CurrentMajorRegion}，时间:{state.CurTime}，章节:{state.CurrentChapter}";
        var protagonistStr = protagonist is null ? "无" :
            $"{protagonist.Name}，位于{protagonist.LocationName}，状态:{protagonist.SelfStatus}，{protagonist.BaseAttributes}{(string.IsNullOrWhiteSpace(protagonist.SpecialAttributes) ? "" : "，" + protagonist.SpecialAttributes)}";
        var npcStr = npcs.Count == 0 ? "（无）" : string.Join("；", npcs);

        return $"[全局状态] {stateStr}\n[主角] {protagonistStr}\n[在册NPC] {npcStr}";
    }
}

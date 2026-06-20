using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Models;

namespace Re0Agent.Core.Services.Dice;

/// <summary>
/// 战斗结算直写库：根据 <see cref="DiceResult"/> 按角色稳定 ID 直接修改
/// protagonist_info / important_npc 的 hp / mp / stamina，不经过填表 Agent。
/// 在每个角色行动后立即结算，使状态栏与后续角色看到的就是最新 HP。
/// </summary>
public sealed class CombatResolver(Re0AgentDbContext dbContext)
{
    public sealed record CombatApplyResult(bool ProtagonistDied, string? DefenderName, int? RemainingHp);

    public async Task<CombatApplyResult> ApplyAsync(
        DiceResult result,
        CancellationToken cancellationToken = default)
    {
        if (!result.IsCombat)
        {
            return new CombatApplyResult(false, null, null);
        }

        // 攻击者消耗魔法/体力（无论是否命中，行动已发生）。
        if (result.AttackerId is > 0 && (result.ManaCost > 0 || result.StaminaCost > 0))
        {
            await SpendResourcesAsync(result.AttackerId.Value, result.ManaCost, result.StaminaCost, cancellationToken);
        }

        // 防御者扣血（仅命中且有有效伤害）。
        if (result.DefenderId is > 0 && result.Damage is > 0)
        {
            return await ApplyDamageAsync(result.DefenderId.Value, result.Damage.Value, cancellationToken);
        }

        return new CombatApplyResult(false, null, null);
    }

    private async Task<CombatApplyResult> ApplyDamageAsync(
        int defenderId,
        int damage,
        CancellationToken cancellationToken)
    {
        var protagonist = await dbContext.ProtagonistInfo
            .FirstOrDefaultAsync(p => p.CharId == defenderId, cancellationToken);
        if (protagonist is not null)
        {
            protagonist.Hp = Math.Max(0, protagonist.Hp - damage);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new CombatApplyResult(protagonist.Hp <= 0, protagonist.Name, protagonist.Hp);
        }

        var npc = await dbContext.ImportantNpcs
            .FirstOrDefaultAsync(n => n.CharId == defenderId, cancellationToken);
        if (npc is not null)
        {
            npc.Hp = Math.Max(0, npc.Hp - damage);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new CombatApplyResult(false, npc.Name, npc.Hp);
        }

        return new CombatApplyResult(false, null, null);
    }

    private async Task SpendResourcesAsync(
        int charId,
        int manaCost,
        int staminaCost,
        CancellationToken cancellationToken)
    {
        var protagonist = await dbContext.ProtagonistInfo
            .FirstOrDefaultAsync(p => p.CharId == charId, cancellationToken);
        if (protagonist is not null)
        {
            protagonist.Mp = Math.Max(0, protagonist.Mp - manaCost);
            protagonist.Stamina = Math.Max(0, protagonist.Stamina - staminaCost);
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        var npc = await dbContext.ImportantNpcs
            .FirstOrDefaultAsync(n => n.CharId == charId, cancellationToken);
        if (npc is not null)
        {
            npc.Mp = Math.Max(0, npc.Mp - manaCost);
            npc.Stamina = Math.Max(0, npc.Stamina - staminaCost);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}

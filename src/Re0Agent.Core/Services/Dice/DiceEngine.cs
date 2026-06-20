using Re0Agent.Core.Models;

namespace Re0Agent.Core.Services.Dice;

/// <summary>
/// 2d6 自动化判定系统 (V2)。
/// 最终达成值 = 2d6之和 + 属性修正((属性-10)/2 向下取整) + 状态/道具加成。
/// 原始 2d6=12→大成功；=2→大失败；否则与目标值(DC)对比分级。
/// 战斗：命中后 伤害 = 武器/技能基础伤害 + 力量修正 - 防御方护甲；带出攻防 ID 供直写库。
/// </summary>
public sealed class DiceEngine(
    DiceCommandParser parser,
    CharacterAttributeProvider attributeProvider,
    IDiceRoller diceRoller)
{
    public async Task<DiceResult> ExecuteAsync(
        string? commandText,
        CancellationToken cancellationToken = default)
    {
        var command = parser.Parse(commandText);
        return await ExecuteAsync(command, cancellationToken);
    }

    public async Task<DiceResult> ExecuteAsync(
        DiceCommand command,
        CancellationToken cancellationToken = default)
    {
        return command.Kind switch
        {
            DiceCommandKind.None => Fixed(command.RawText, "无需检定", true, "无"),
            DiceCommandKind.AutoSuccess => Fixed(command.RawText, "成功", true, "必成"),
            DiceCommandKind.AutoFailure => Fixed(command.RawText, "失败", false, "必败"),
            DiceCommandKind.Check => await ExecuteCheckAsync(command, cancellationToken),
            DiceCommandKind.Opposed => await ExecuteOpposedAsync(command, cancellationToken),
            DiceCommandKind.Attack => await ExecuteAttackAsync(command, cancellationToken),
            DiceCommandKind.Saving => await ExecuteCheckAsync(command, cancellationToken),
            DiceCommandKind.Authority => await ExecuteAuthorityAsync(command, cancellationToken),
            DiceCommandKind.Miasma => ExecuteMiasma(command),
            _ => Invalid(command.RawText, command.Error ?? "无效骰子命令")
        };
    }

    /// <summary>属性修正 = (属性值 - 10) / 2 向下取整。普通人(10)修正为0。</summary>
    public static int AttributeModifier(int attributeValue)
    {
        return (int)Math.Floor((attributeValue - 10) / 2.0);
    }

    private async Task<DiceResult> ExecuteAuthorityAsync(
        DiceCommand command,
        CancellationToken cancellationToken)
    {
        if (command.AuthorityType == "死亡回归")
        {
            return new DiceResult
            {
                Command = command.RawText,
                RollerName = command.RollerName,
                AttributeName = command.AttributeName,
                Outcome = "自动触发",
                Detail = "死亡回归无需检定；数据库回滚留到Phase4。",
                IsSuccess = true,
                SuccessLevel = "自动",
                Tags = new Dictionary<string, string> { ["权能类型"] = "死亡回归" }
            };
        }

        if (command.AuthorityType == "狮子的心脏")
        {
            return new DiceResult
            {
                Command = command.RawText,
                RollerName = command.RollerName,
                AttributeName = command.AttributeName,
                Outcome = "成功",
                Detail = "狮子的心脏按设定视为必成；持续时间由后续剧情处理。",
                IsSuccess = true,
                SuccessLevel = "必成",
                Tags = new Dictionary<string, string> { ["权能类型"] = "狮子的心脏" }
            };
        }

        var result = await ExecuteCheckAsync(command, cancellationToken);
        var tags = result.Tags.ToDictionary(StringComparer.Ordinal);
        tags["权能类型"] = command.AuthorityType ?? "未指定";
        return Clone(result, tags: tags);
    }

    private DiceResult ExecuteMiasma(DiceCommand command)
    {
        var currentLevel = command.CurrentMiasmaLevel ?? 0;
        var target = Math.Clamp(100 - currentLevel, 0, 100);
        var roll = diceRoller.RollD100();
        var isBigFailure = roll == 100 || (target < 50 && roll >= 96);
        var isSuccess = roll <= target && !isBigFailure;
        var increment = isSuccess ? 5 : isBigFailure ? 30 : 15;
        var newLevel = Math.Clamp(currentLevel + increment, 0, 100);

        return new DiceResult
        {
            Command = command.RawText,
            Roll = roll,
            Rolls = [roll],
            Target = target,
            TargetAfterModifiers = target,
            Outcome = isSuccess ? "成功" : isBigFailure ? "大失败" : "失败",
            Detail = $"瘴气从{currentLevel}提升到{newLevel}，增量+{increment}。",
            IsSuccess = isSuccess,
            SuccessLevel = isBigFailure ? "大失败" : isSuccess ? "普通成功" : "失败",
            Tags = new Dictionary<string, string>
            {
                ["当前瘴气"] = currentLevel.ToString(),
                ["新增瘴气"] = increment.ToString(),
                ["新瘴气"] = newLevel.ToString()
            }
        };
    }

    private async Task<DiceResult> ExecuteCheckAsync(
        DiceCommand command,
        CancellationToken cancellationToken)
    {
        var attribute = await ResolveActorAsync(
            command.RollerId, command.RollerName, command.AttributeName, cancellationToken);

        if (attribute is null)
        {
            return Invalid(command.RawText, $"找不到可投骰属性：{Describe(command.RollerId, command.RollerName)}/{command.AttributeName}");
        }

        var (sum, rolls) = Roll2d6();
        var modifier = AttributeModifier(attribute.Value);
        var total = sum + modifier + command.SituationBonus;
        var evaluation = Evaluate(sum, total, command.TargetValue);

        var label = command.Kind == DiceCommandKind.Saving
            ? $"{command.SaveType}豁免"
            : $"{attribute.AttributeName}检定";

        return new DiceResult
        {
            Command = command.RawText,
            RollerName = attribute.CharacterName,
            AttributeName = attribute.AttributeName,
            Roll = sum,
            RawRollSum = sum,
            AttributeModifier = modifier,
            Rolls = rolls,
            Target = command.TargetValue,
            TargetAfterModifiers = total,
            Outcome = evaluation.IsSuccess ? "成功" : evaluation.Name,
            Detail = $"{attribute.CharacterName}进行{label}：2d6({rolls[0]}+{rolls[1]})={sum}，属性修正{Sign(modifier)}{(command.SituationBonus != 0 ? $"，加成{Sign(command.SituationBonus)}" : "")}，最终达成值{total}/DC{command.TargetValue}，{evaluation.Name}。",
            IsSuccess = evaluation.IsSuccess,
            SuccessLevel = evaluation.Name,
            Tags = CreateBaseTags(command)
        };
    }

    private async Task<DiceResult> ExecuteOpposedAsync(
        DiceCommand command,
        CancellationToken cancellationToken)
    {
        var attacker = await ResolveActorAsync(
            command.RollerId, command.RollerName, command.AttributeName, cancellationToken);
        var defender = await ResolveActorAsync(
            command.OpponentId, command.OpponentName, command.OpponentAttributeName, cancellationToken);

        if (attacker is null || defender is null)
        {
            return Invalid(command.RawText, $"找不到对抗检定属性：{Describe(command.RollerId, command.RollerName)} vs {Describe(command.OpponentId, command.OpponentName)}");
        }

        var (attackerSum, attackerRolls) = Roll2d6();
        var (defenderSum, defenderRolls) = Roll2d6();
        var attackerMod = AttributeModifier(attacker.Value);
        var defenderMod = AttributeModifier(defender.Value);
        var attackerTotal = attackerSum + attackerMod + command.SituationBonus;
        var defenderTotal = defenderSum + defenderMod;

        var attackerWins = attackerSum == 12
            || (defenderSum != 12 && attackerTotal >= defenderTotal && attackerSum != 2);

        var tags = CreateBaseTags(command);
        tags["防守方"] = defender.CharacterName;
        tags["防守属性"] = defender.AttributeName;
        tags["防守达成值"] = defenderTotal.ToString();

        return new DiceResult
        {
            Command = command.RawText,
            RollerName = attacker.CharacterName,
            AttributeName = attacker.AttributeName,
            Roll = attackerSum,
            RawRollSum = attackerSum,
            AttributeModifier = attackerMod,
            Rolls = attackerRolls.Concat(defenderRolls).ToArray(),
            Target = defenderTotal,
            TargetAfterModifiers = attackerTotal,
            Outcome = attackerWins ? "成功" : "失败",
            Detail = $"{attacker.CharacterName}(2d6={attackerSum}{Sign(attackerMod)}={attackerTotal}) vs {defender.CharacterName}(2d6={defenderSum}{Sign(defenderMod)}={defenderTotal})：{(attackerWins ? "发起方胜" : "防守方胜")}。",
            IsSuccess = attackerWins,
            SuccessLevel = attackerSum == 12 ? "大成功" : attackerSum == 2 ? "大失败" : attackerWins ? "成功" : "失败",
            Tags = tags
        };
    }

    private async Task<DiceResult> ExecuteAttackAsync(
        DiceCommand command,
        CancellationToken cancellationToken)
    {
        var attacker = await ResolveActorAsync(
            command.RollerId, command.RollerName, command.AttributeName, cancellationToken);
        var defender = await ResolveActorAsync(
            command.OpponentId, command.OpponentName, command.OpponentAttributeName, cancellationToken);

        if (attacker is null || defender is null)
        {
            return Invalid(command.RawText, $"找不到战斗双方属性：{Describe(command.RollerId, command.RollerName)} vs {Describe(command.OpponentId, command.OpponentName)}");
        }

        var (attackerSum, attackerRolls) = Roll2d6();
        var (defenderSum, defenderRolls) = Roll2d6();
        var attackerMod = AttributeModifier(attacker.Value);
        var defenderMod = AttributeModifier(defender.Value);
        var attackerTotal = attackerSum + attackerMod + command.SituationBonus;
        var defenderTotal = defenderSum + defenderMod;

        // 命中：原始12必中；原始2必失；否则达成值≥防御达成值。
        var hit = attackerSum == 12 || (defenderSum != 12 && attackerSum != 2 && attackerTotal >= defenderTotal);

        // 力量修正参与伤害（攻击属性即便是敏捷，伤害仍按力量修正，对齐世界书公式）。
        var strength = await ResolveActorAsync(attacker.CharId, attacker.CharacterName, "力量", cancellationToken);
        var strengthMod = strength is null ? attackerMod : AttributeModifier(strength.Value);
        var rawDamage = command.WeaponDamage + strengthMod - defender.Armor;
        var damage = hit ? Math.Max(0, rawDamage) : 0;
        var blocked = hit && damage <= 0;

        var tags = CreateBaseTags(command);
        tags["防守方"] = defender.CharacterName;
        tags["防守达成值"] = defenderTotal.ToString();
        tags["攻击者ID"] = attacker.CharId.ToString();
        tags["防御者ID"] = defender.CharId.ToString();
        if (hit)
        {
            tags["伤害"] = damage.ToString();
            tags["护甲"] = defender.Armor.ToString();
        }
        if (!string.IsNullOrWhiteSpace(command.SkillName))
        {
            tags["技能"] = command.SkillName!;
        }

        var successLevel = attackerSum == 12 ? "大成功" : attackerSum == 2 ? "大失败" : hit ? "命中" : "未命中";
        var detail = hit
            ? (blocked
                ? $"{attacker.CharacterName}命中{defender.CharacterName}，但伤害({command.WeaponDamage}{Sign(strengthMod)})未击穿护甲({defender.Armor})，无效。"
                : $"{attacker.CharacterName}命中{defender.CharacterName}，造成{damage}点伤害(基础{command.WeaponDamage}+力量修正{Sign(strengthMod)}-护甲{defender.Armor})。")
            : $"{attacker.CharacterName}的攻击被{defender.CharacterName}闪避(2d6={attackerSum}{Sign(attackerMod)}={attackerTotal} < {defenderTotal})。";

        return new DiceResult
        {
            Command = command.RawText,
            RollerName = attacker.CharacterName,
            AttributeName = attacker.AttributeName,
            Roll = attackerSum,
            RawRollSum = attackerSum,
            AttributeModifier = attackerMod,
            Rolls = attackerRolls.Concat(defenderRolls).ToArray(),
            Target = defenderTotal,
            TargetAfterModifiers = attackerTotal,
            Outcome = hit ? (blocked ? "格挡" : "命中") : "未命中",
            Detail = detail,
            IsSuccess = hit && !blocked,
            SuccessLevel = successLevel,
            IsCombat = true,
            AttackerId = attacker.CharId,
            DefenderId = defender.CharId,
            Damage = damage,
            ManaCost = command.ManaCost,
            StaminaCost = command.StaminaCost,
            Tags = tags
        };
    }

    private async Task<CharacterAttribute?> ResolveActorAsync(
        int? charId,
        string? name,
        string? attributeName,
        CancellationToken cancellationToken)
    {
        if (charId is > 0)
        {
            return await attributeProvider.FindAttributeByIdAsync(charId.Value, attributeName, cancellationToken);
        }

        return await attributeProvider.FindAttributeAsync(name, attributeName, cancellationToken);
    }

    private (int Sum, IReadOnlyList<int> Rolls) Roll2d6()
    {
        var a = diceRoller.RollD6();
        var b = diceRoller.RollD6();
        return (a + b, [a, b]);
    }

    private static CheckEvaluation Evaluate(int rawSum, int total, int dc)
    {
        if (rawSum == 12)
        {
            return new CheckEvaluation("大成功", true);
        }

        if (rawSum == 2)
        {
            return new CheckEvaluation("大失败", false);
        }

        if (total >= dc + 5)
        {
            return new CheckEvaluation("完全成功", true);
        }

        if (total >= dc)
        {
            return new CheckEvaluation("成功", true);
        }

        return new CheckEvaluation("失败", false);
    }

    private static string Sign(int value)
    {
        return value >= 0 ? $"+{value}" : value.ToString();
    }

    private static string Describe(int? id, string? name)
    {
        return id is > 0 ? $"#{id}" : name ?? "?";
    }

    private static Dictionary<string, string> CreateBaseTags(DiceCommand command)
    {
        var tags = new Dictionary<string, string>();
        if (command.SituationBonus != 0)
        {
            tags["加成"] = command.SituationBonus.ToString();
        }

        return tags;
    }

    private static DiceResult Fixed(string command, string outcome, bool isSuccess, string detail)
    {
        return new DiceResult
        {
            Command = command,
            Outcome = outcome,
            Detail = detail,
            IsSuccess = isSuccess,
            SuccessLevel = outcome
        };
    }

    private static DiceResult Invalid(string command, string error)
    {
        return new DiceResult
        {
            Command = command,
            Outcome = "无效",
            Detail = error,
            IsSuccess = false,
            Error = error,
            SuccessLevel = "无效"
        };
    }

    private static DiceResult Clone(
        DiceResult result,
        string? detail = null,
        IReadOnlyList<int>? rolls = null,
        IReadOnlyDictionary<string, string>? tags = null)
    {
        return new DiceResult
        {
            Command = result.Command,
            RollerName = result.RollerName,
            AttributeName = result.AttributeName,
            Roll = result.Roll,
            RawRollSum = result.RawRollSum,
            AttributeModifier = result.AttributeModifier,
            Rolls = rolls ?? result.Rolls,
            Target = result.Target,
            TargetAfterModifiers = result.TargetAfterModifiers,
            Outcome = result.Outcome,
            Detail = detail ?? result.Detail,
            IsSuccess = result.IsSuccess,
            SuccessLevel = result.SuccessLevel,
            RequiredLevel = result.RequiredLevel,
            Error = result.Error,
            IsCombat = result.IsCombat,
            AttackerId = result.AttackerId,
            DefenderId = result.DefenderId,
            Damage = result.Damage,
            ManaCost = result.ManaCost,
            StaminaCost = result.StaminaCost,
            Tags = tags ?? result.Tags
        };
    }

    private sealed record CheckEvaluation(string Name, bool IsSuccess);
}

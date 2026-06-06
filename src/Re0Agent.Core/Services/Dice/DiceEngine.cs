using Re0Agent.Core.Models;

namespace Re0Agent.Core.Services.Dice;

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
            DiceCommandKind.Check => await ExecuteCheckAsync(command, modifier: 0, cancellationToken),
            DiceCommandKind.Opposed => await ExecuteOpposedAsync(command, attackerModifier: 0, cancellationToken),
            DiceCommandKind.Magic => await ExecuteMagicAsync(command, cancellationToken),
            DiceCommandKind.SpiritArt => await ExecuteSpiritArtAsync(command, cancellationToken),
            DiceCommandKind.Authority => await ExecuteAuthorityAsync(command, cancellationToken),
            DiceCommandKind.Miasma => ExecuteMiasma(command),
            DiceCommandKind.Blessing => await ExecuteBlessingAsync(command, cancellationToken),
            DiceCommandKind.MagicOpposed => await ExecuteOpposedAsync(command, command.ElementAdvantage ? 10 : 0, cancellationToken),
            _ => Invalid(command.RawText, command.Error ?? "无效骰子命令")
        };
    }

    private async Task<DiceResult> ExecuteMagicAsync(
        DiceCommand command,
        CancellationToken cancellationToken)
    {
        var modifier = command.MagicLevel switch
        {
            "El" => -10,
            "Ul" => -20,
            "Al" => -30,
            _ => 0
        };

        var result = await ExecuteCheckAsync(command, modifier, cancellationToken);
        var tags = result.Tags.ToDictionary(StringComparer.Ordinal);
        tags["魔法等级"] = command.MagicLevel;

        if (result.IsSuccess == true && !string.IsNullOrWhiteSpace(command.GateAttributeName))
        {
            var gateAttribute = await attributeProvider.FindAttributeAsync(
                command.RollerName,
                command.GateAttributeName,
                cancellationToken);

            if (gateAttribute is null)
            {
                tags["门消耗"] = "门属性未找到";
            }
            else
            {
                var gateRoll = Roll(command.BonusPenalty);
                var gateEvaluation = EvaluateRoll(gateRoll.SelectedRoll, gateAttribute.Value, "普通");
                tags["门消耗"] = gateEvaluation.IsSuccess ? "无新增损伤" : "损伤等级+1";

                return Clone(
                    result,
                    detail: $"{result.Detail}；门消耗检定 {gateRoll.SelectedRoll}/{gateAttribute.Value}：{tags["门消耗"]}",
                    rolls: result.Rolls.Concat(gateRoll.Rolls).ToArray(),
                    tags: tags);
            }
        }

        return Clone(result, tags: tags);
    }

    private async Task<DiceResult> ExecuteSpiritArtAsync(
        DiceCommand command,
        CancellationToken cancellationToken)
    {
        if (command.IsActive == false)
        {
            return new DiceResult
            {
                Command = command.RawText,
                RollerName = command.RollerName,
                AttributeName = command.AttributeName,
                Outcome = "失败",
                Detail = "精灵不在活跃时间，精灵术自动失败。",
                IsSuccess = false,
                SuccessLevel = "失败",
                Tags = new Dictionary<string, string> { ["精灵活跃"] = "否" }
            };
        }

        var result = await ExecuteCheckAsync(command, modifier: 0, cancellationToken);
        var tags = result.Tags.ToDictionary(StringComparer.Ordinal);
        tags["精灵活跃"] = command.IsActive is null ? "未指定" : "是";
        return Clone(result, tags: tags);
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

        var result = await ExecuteCheckAsync(command, modifier: 0, cancellationToken);
        var tags = result.Tags.ToDictionary(StringComparer.Ordinal);
        tags["权能类型"] = command.AuthorityType ?? "未指定";
        if (command.AuthorityType == "Invisible Providence" && result.IsSuccess == true)
        {
            tags["反噬"] = "成功也会承受1d20生命损失；Phase3不写DB。";
        }

        return Clone(result, tags: tags);
    }

    private DiceResult ExecuteMiasma(DiceCommand command)
    {
        var currentLevel = command.CurrentMiasmaLevel ?? 0;
        var target = Math.Clamp(100 - currentLevel, 0, 100);
        var roll = diceRoller.RollD100();
        var isBigFailure = IsBigFailure(roll, target);
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

    private async Task<DiceResult> ExecuteBlessingAsync(
        DiceCommand command,
        CancellationToken cancellationToken)
    {
        if (command.CountersAuthority)
        {
            return new DiceResult
            {
                Command = command.RawText,
                RollerName = command.RollerName,
                AttributeName = command.AttributeName,
                Outcome = "失败",
                Detail = "加护对抗权能时自动失败。",
                IsSuccess = false,
                SuccessLevel = "失败",
                Tags = new Dictionary<string, string> { ["对抗权能"] = "是" }
            };
        }

        var result = await ExecuteCheckAsync(command, modifier: 0, cancellationToken);
        var tags = result.Tags.ToDictionary(StringComparer.Ordinal);
        tags["对抗权能"] = "否";
        return Clone(result, tags: tags);
    }

    private async Task<DiceResult> ExecuteCheckAsync(
        DiceCommand command,
        int modifier,
        CancellationToken cancellationToken)
    {
        var attribute = await attributeProvider.FindAttributeAsync(
            command.RollerName,
            command.AttributeName,
            cancellationToken);

        if (attribute is null)
        {
            return Invalid(command.RawText, $"找不到可投骰属性：{command.RollerName}/{command.AttributeName}");
        }

        var target = Math.Clamp(attribute.Value + modifier, 0, 100);
        var roll = Roll(command.BonusPenalty);
        var evaluation = EvaluateRoll(roll.SelectedRoll, target, command.Difficulty);

        return new DiceResult
        {
            Command = command.RawText,
            RollerName = attribute.CharacterName,
            AttributeName = attribute.AttributeName,
            Roll = roll.SelectedRoll,
            Rolls = roll.Rolls,
            Target = attribute.Value,
            TargetAfterModifiers = target,
            Outcome = evaluation.IsSuccess ? "成功" : evaluation.SuccessLevel,
            Detail = $"{attribute.CharacterName}以{attribute.AttributeName}进行检定：{roll.SelectedRoll}/{target}，{evaluation.SuccessLevel}。",
            IsSuccess = evaluation.IsSuccess,
            SuccessLevel = evaluation.SuccessLevel,
            RequiredLevel = command.Difficulty,
            Tags = CreateBaseTags(command, modifier)
        };
    }

    private async Task<DiceResult> ExecuteOpposedAsync(
        DiceCommand command,
        int attackerModifier,
        CancellationToken cancellationToken)
    {
        var attacker = await attributeProvider.FindAttributeAsync(
            command.RollerName,
            command.AttributeName,
            cancellationToken);
        var defender = await attributeProvider.FindAttributeAsync(
            command.OpponentName,
            command.OpponentAttributeName,
            cancellationToken);

        if (attacker is null || defender is null)
        {
            return Invalid(command.RawText, $"找不到对抗检定属性：{command.RollerName}/{command.AttributeName} vs {command.OpponentName}/{command.OpponentAttributeName}");
        }

        var attackerTarget = Math.Clamp(attacker.Value + attackerModifier, 0, 100);
        var attackerRoll = Roll(command.BonusPenalty);
        var defenderRoll = Roll(null);
        var attackerEvaluation = EvaluateRoll(attackerRoll.SelectedRoll, attackerTarget, command.Difficulty);
        var defenderEvaluation = EvaluateRoll(defenderRoll.SelectedRoll, defender.Value, "普通");

        var attackerRank = attackerEvaluation.IsSuccess ? attackerEvaluation.Rank : 0;
        var defenderRank = defenderEvaluation.IsSuccess ? defenderEvaluation.Rank : 0;
        var attackerWins = attackerRank > defenderRank
            || (attackerRank == defenderRank
                && attackerRank > 0
                && attackerRoll.SelectedRoll < defenderRoll.SelectedRoll);

        var tags = CreateBaseTags(command, attackerModifier);
        tags["防守方"] = defender.CharacterName;
        tags["防守属性"] = defender.AttributeName;
        tags["防守骰值"] = defenderRoll.SelectedRoll.ToString();
        tags["防守成功等级"] = defenderEvaluation.SuccessLevel;

        return new DiceResult
        {
            Command = command.RawText,
            RollerName = attacker.CharacterName,
            AttributeName = attacker.AttributeName,
            Roll = attackerRoll.SelectedRoll,
            Rolls = attackerRoll.Rolls.Concat(defenderRoll.Rolls).ToArray(),
            Target = attacker.Value,
            TargetAfterModifiers = attackerTarget,
            Outcome = attackerWins ? "成功" : "失败",
            Detail = $"{attacker.CharacterName}({attackerRoll.SelectedRoll}/{attackerTarget},{attackerEvaluation.SuccessLevel}) vs {defender.CharacterName}({defenderRoll.SelectedRoll}/{defender.Value},{defenderEvaluation.SuccessLevel})。",
            IsSuccess = attackerWins,
            SuccessLevel = attackerEvaluation.SuccessLevel,
            RequiredLevel = command.Difficulty,
            Tags = tags
        };
    }

    private RollResult Roll(string? bonusPenalty)
    {
        if (bonusPenalty is "奖励1")
        {
            var rolls = new[] { diceRoller.RollD100(), diceRoller.RollD100() };
            return new RollResult(rolls.Min(), rolls);
        }

        if (bonusPenalty is "惩罚1")
        {
            var rolls = new[] { diceRoller.RollD100(), diceRoller.RollD100() };
            return new RollResult(rolls.Max(), rolls);
        }

        var roll = diceRoller.RollD100();
        return new RollResult(roll, [roll]);
    }

    private static CheckEvaluation EvaluateRoll(int roll, int target, string difficulty)
    {
        var level = ReadSuccessLevel(roll, target);
        var requiredRank = DifficultyRank(difficulty);
        var isSuccess = level.Rank >= requiredRank;
        return new CheckEvaluation(level.Name, level.Rank, isSuccess);
    }

    private static (string Name, int Rank) ReadSuccessLevel(int roll, int target)
    {
        if (roll == 1)
        {
            return ("大成功", 4);
        }

        if (IsBigFailure(roll, target))
        {
            return ("大失败", -1);
        }

        if (roll > target)
        {
            return ("失败", 0);
        }

        if (roll <= target / 5)
        {
            return ("极难成功", 3);
        }

        if (roll <= target / 2)
        {
            return ("困难成功", 2);
        }

        return ("普通成功", 1);
    }

    private static bool IsBigFailure(int roll, int target)
    {
        return roll == 100 || (target < 50 && roll >= 96);
    }

    private static int DifficultyRank(string difficulty)
    {
        return difficulty switch
        {
            "极难" => 3,
            "困难" => 2,
            _ => 1
        };
    }

    private static Dictionary<string, string> CreateBaseTags(DiceCommand command, int modifier)
    {
        var tags = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(command.BonusPenalty))
        {
            tags["奖惩"] = command.BonusPenalty;
        }

        if (modifier != 0)
        {
            tags["修正"] = modifier.ToString();
        }

        if (command.ElementAdvantage)
        {
            tags["相克"] = "是";
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
            Rolls = rolls ?? result.Rolls,
            Target = result.Target,
            TargetAfterModifiers = result.TargetAfterModifiers,
            Outcome = result.Outcome,
            Detail = detail ?? result.Detail,
            IsSuccess = result.IsSuccess,
            SuccessLevel = result.SuccessLevel,
            RequiredLevel = result.RequiredLevel,
            Error = result.Error,
            Tags = tags ?? result.Tags
        };
    }

    private sealed record RollResult(int SelectedRoll, IReadOnlyList<int> Rolls);

    private sealed record CheckEvaluation(string SuccessLevel, int Rank, bool IsSuccess);
}

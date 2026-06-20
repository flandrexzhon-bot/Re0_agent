using System.Text.RegularExpressions;

namespace Re0Agent.Core.Services.Dice;

public sealed partial class DiceCommandParser
{
    public DiceCommand Parse(string? text)
    {
        var commandText = NormalizeCommandText(text);
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return new DiceCommand { RawText = "无", Kind = DiceCommandKind.None };
        }

        if (commandText.StartsWith("无", StringComparison.Ordinal))
        {
            return new DiceCommand { RawText = "无", Kind = DiceCommandKind.None };
        }

        if (commandText.StartsWith("必成", StringComparison.Ordinal))
        {
            return new DiceCommand { RawText = "必成", Kind = DiceCommandKind.AutoSuccess };
        }

        if (commandText.StartsWith("必败", StringComparison.Ordinal))
        {
            return new DiceCommand { RawText = "必败", Kind = DiceCommandKind.AutoFailure };
        }

        var tokens = commandText
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        if (tokens.Length == 0)
        {
            return new DiceCommand { RawText = commandText, Kind = DiceCommandKind.None };
        }

        return tokens[0] switch
        {
            "检定" => ParseCheck(commandText, tokens),
            "对抗" => ParseOpposed(commandText, tokens),
            "攻击" => ParseAttack(commandText, tokens),
            "豁免" => ParseSaving(commandText, tokens),
            "权能" => ParseAuthority(commandText, tokens),
            "瘴气" => ParseMiasma(commandText, tokens),
            _ => Invalid(commandText, $"未知骰子命令：{tokens[0]}")
        };
    }

    public static string NormalizeCommandText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "无";
        }

        var value = text.Trim();
        // 剥离 <content>/<thought> 等标签外壳，避免标签混入判定指令。
        value = TagRegex().Replace(value, " ").Trim();
        var judgementMatch = JudgementRegex().Match(value);
        if (judgementMatch.Success)
        {
            value = judgementMatch.Groups["command"].Value;
        }

        return value.Trim().TrimEnd('。', '；', ';', '，', ',', '.');
    }

    private static DiceCommand ParseCheck(string rawText, IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 3)
        {
            return Invalid(rawText, "普通检定格式应为：检定 <角色|#ID> <属性> [目标值=N]");
        }

        var (name, id) = ResolveActor(tokens[1]);
        return new DiceCommand
        {
            RawText = rawText,
            Kind = DiceCommandKind.Check,
            RollerName = name,
            RollerId = id,
            AttributeName = tokens[2],
            TargetValue = ReadIntOption(tokens, "目标值=") ?? 10,
            SituationBonus = ReadIntOption(tokens, "加成=") ?? 0
        };
    }

    private static DiceCommand ParseOpposed(string rawText, IReadOnlyList<string> tokens)
    {
        var vsIndex = Array.FindIndex(tokens.ToArray(), token => token.Equals("vs", StringComparison.OrdinalIgnoreCase));
        if (tokens.Count < 6 || vsIndex != 3 || vsIndex + 2 >= tokens.Count)
        {
            return Invalid(rawText, "对抗检定格式应为：对抗 <角色|#ID> <属性> vs <角色|#ID> <属性>");
        }

        var (name, id) = ResolveActor(tokens[1]);
        var (oppName, oppId) = ResolveActor(tokens[vsIndex + 1]);
        return new DiceCommand
        {
            RawText = rawText,
            Kind = DiceCommandKind.Opposed,
            RollerName = name,
            RollerId = id,
            AttributeName = tokens[2],
            OpponentName = oppName,
            OpponentId = oppId,
            OpponentAttributeName = tokens[vsIndex + 2],
            SituationBonus = ReadIntOption(tokens, "加成=") ?? 0
        };
    }

    private static DiceCommand ParseAttack(string rawText, IReadOnlyList<string> tokens)
    {
        var vsIndex = Array.FindIndex(tokens.ToArray(), token => token.Equals("vs", StringComparison.OrdinalIgnoreCase));
        if (tokens.Count < 4 || vsIndex != 2 || vsIndex + 1 >= tokens.Count)
        {
            return Invalid(rawText, "战斗格式应为：攻击 <攻击者|#ID> vs <防御者|#ID> [武器伤害=N] [技能=名] [魔耗=N] [体耗=N] [攻击属性=力量|敏捷]");
        }

        var (attackerName, attackerId) = ResolveActor(tokens[1]);
        var (defenderName, defenderId) = ResolveActor(tokens[vsIndex + 1]);
        return new DiceCommand
        {
            RawText = rawText,
            Kind = DiceCommandKind.Attack,
            RollerName = attackerName,
            RollerId = attackerId,
            AttributeName = ReadOption(tokens, "攻击属性=") ?? "力量",
            OpponentName = defenderName,
            OpponentId = defenderId,
            OpponentAttributeName = "敏捷",
            WeaponDamage = ReadIntOption(tokens, "武器伤害=") ?? ReadIntOption(tokens, "伤害=") ?? 0,
            SkillName = ReadOption(tokens, "技能="),
            ManaCost = ReadIntOption(tokens, "魔耗=") ?? 0,
            StaminaCost = ReadIntOption(tokens, "体耗=") ?? 0,
            SituationBonus = ReadIntOption(tokens, "加成=") ?? 0
        };
    }

    private static DiceCommand ParseSaving(string rawText, IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 3)
        {
            return Invalid(rawText, "豁免格式应为：豁免 <角色|#ID> <魔法|精神|权能> [目标值=N]");
        }

        var (name, id) = ResolveActor(tokens[1]);
        return new DiceCommand
        {
            RawText = rawText,
            Kind = DiceCommandKind.Saving,
            RollerName = name,
            RollerId = id,
            AttributeName = "精神",
            SaveType = tokens[2],
            TargetValue = ReadIntOption(tokens, "目标值=") ?? DefaultSaveDc(tokens[2]),
            SituationBonus = ReadIntOption(tokens, "加成=") ?? 0
        };
    }

    private static DiceCommand ParseAuthority(string rawText, IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 2)
        {
            return Invalid(rawText, "权能格式应为：权能 <角色|#ID> [类型=死亡回归|狮子的心脏|...]");
        }

        var (name, id) = ResolveActor(tokens[1]);
        return new DiceCommand
        {
            RawText = rawText,
            Kind = DiceCommandKind.Authority,
            RollerName = name,
            RollerId = id,
            AttributeName = tokens.Count >= 3 ? tokens[2] : "精神",
            AuthorityType = ReadAuthorityType(rawText) ?? ReadOption(tokens, "类型="),
            TargetValue = ReadIntOption(tokens, "目标值=") ?? 20
        };
    }

    private static DiceCommand ParseMiasma(string rawText, IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 2 || !int.TryParse(tokens[1], out var level))
        {
            return Invalid(rawText, "瘴气检定格式应为：瘴气 <当前等级>");
        }

        return new DiceCommand
        {
            RawText = rawText,
            Kind = DiceCommandKind.Miasma,
            CurrentMiasmaLevel = Math.Clamp(level, 0, 100)
        };
    }

    /// <summary>把 token 解析为 (名字, ID?)。# 前缀表示 CharId；否则按名字。</summary>
    private static (string? Name, int? Id) ResolveActor(string token)
    {
        var t = token.Trim();
        if (t.StartsWith('#') && int.TryParse(t[1..], out var id))
        {
            return (null, id);
        }

        return (t, null);
    }

    private static int DefaultSaveDc(string saveType)
    {
        return saveType switch
        {
            "权能" => 20,
            _ => 10
        };
    }

    private static string? ReadOption(IEnumerable<string> tokens, string prefix)
    {
        var token = tokens.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));
        return token is null ? null : token[prefix.Length..];
    }

    private static int? ReadIntOption(IEnumerable<string> tokens, string prefix)
    {
        var raw = ReadOption(tokens, prefix);
        return raw is not null && int.TryParse(raw, out var value) ? value : null;
    }

    private static string? ReadAuthorityType(string rawText)
    {
        var match = AuthorityTypeRegex().Match(rawText);
        return match.Success ? match.Groups["type"].Value.Trim() : null;
    }

    private static DiceCommand Invalid(string rawText, string error)
    {
        return new DiceCommand
        {
            RawText = rawText,
            Kind = DiceCommandKind.Invalid,
            Error = error
        };
    }

    [GeneratedRegex("判定\\s*[:：]\\s*(?<command>[^\\r\\n。；;<]+)", RegexOptions.Compiled)]
    private static partial Regex JudgementRegex();

    [GeneratedRegex("</?[a-zA-Z_][^>]*>", RegexOptions.Compiled)]
    private static partial Regex TagRegex();

    [GeneratedRegex("类型=(?<type>死亡回归|Invisible Providence|Cor Leonis|狮子的心脏)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex AuthorityTypeRegex();
}

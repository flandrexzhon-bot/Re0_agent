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
            .Where(token => !IsLegacyToken(token))
            .ToArray();

        if (tokens.Length == 0)
        {
            return new DiceCommand { RawText = commandText, Kind = DiceCommandKind.None };
        }

        return tokens[0] switch
        {
            "检定" => ParseCheck(commandText, tokens),
            "对抗" => ParseOpposed(commandText, tokens, DiceCommandKind.Opposed),
            "魔法" => ParseMagic(commandText, tokens),
            "精灵术" => ParseSpiritArt(commandText, tokens),
            "权能" => ParseAuthority(commandText, tokens),
            "瘴气" => ParseMiasma(commandText, tokens),
            "加护" => ParseBlessing(commandText, tokens),
            "魔法对抗" => ParseOpposed(commandText, tokens, DiceCommandKind.MagicOpposed),
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
        var judgementMatch = JudgementRegex().Match(value);
        if (judgementMatch.Success)
        {
            value = judgementMatch.Groups["command"].Value;
        }

        return value
            .Replace(".r1d100", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("平局=发起方失败", string.Empty, StringComparison.Ordinal)
            .Trim()
            .TrimEnd('。', '；', ';', '，', ',', '.');
    }

    private static DiceCommand ParseCheck(string rawText, IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 3)
        {
            return Invalid(rawText, "普通检定格式应为：检定 <角色> <属性>");
        }

        return new DiceCommand
        {
            RawText = rawText,
            Kind = DiceCommandKind.Check,
            RollerName = tokens[1],
            AttributeName = tokens[2],
            Difficulty = ReadOption(tokens, "难度=") ?? "普通",
            BonusPenalty = ReadOption(tokens, "奖惩=")
        };
    }

    private static DiceCommand ParseOpposed(
        string rawText,
        IReadOnlyList<string> tokens,
        DiceCommandKind kind)
    {
        var vsIndex = Array.FindIndex(tokens.ToArray(), token => token.Equals("vs", StringComparison.OrdinalIgnoreCase));
        if (tokens.Count < 6 || vsIndex != 3 || vsIndex + 2 >= tokens.Count)
        {
            return Invalid(rawText, "对抗检定格式应为：对抗 <角色> <属性> vs <角色> <属性>");
        }

        return new DiceCommand
        {
            RawText = rawText,
            Kind = kind,
            RollerName = tokens[1],
            AttributeName = tokens[2],
            OpponentName = tokens[vsIndex + 1],
            OpponentAttributeName = tokens[vsIndex + 2],
            Difficulty = ReadOption(tokens, "难度=") ?? "普通",
            BonusPenalty = ReadOption(tokens, "奖惩="),
            ElementAdvantage = ReadYesNoOption(tokens, "相克=") ?? false
        };
    }

    private static DiceCommand ParseMagic(string rawText, IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 3)
        {
            return Invalid(rawText, "魔法检定格式应为：魔法 <角色> <属性>");
        }

        return new DiceCommand
        {
            RawText = rawText,
            Kind = DiceCommandKind.Magic,
            RollerName = tokens[1],
            AttributeName = tokens[2],
            MagicLevel = ReadOption(tokens, "等级=") ?? "基础",
            GateAttributeName = ReadOption(tokens, "门="),
            BonusPenalty = ReadOption(tokens, "奖惩=")
        };
    }

    private static DiceCommand ParseSpiritArt(string rawText, IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 3)
        {
            return Invalid(rawText, "精灵术检定格式应为：精灵术 <角色> <契约属性>");
        }

        return new DiceCommand
        {
            RawText = rawText,
            Kind = DiceCommandKind.SpiritArt,
            RollerName = tokens[1],
            AttributeName = tokens[2],
            IsActive = ReadYesNoOption(tokens, "活跃="),
            BonusPenalty = ReadOption(tokens, "奖惩=")
        };
    }

    private static DiceCommand ParseAuthority(string rawText, IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 3)
        {
            return Invalid(rawText, "权能检定格式应为：权能 <角色> <属性>");
        }

        return new DiceCommand
        {
            RawText = rawText,
            Kind = DiceCommandKind.Authority,
            RollerName = tokens[1],
            AttributeName = tokens[2],
            AuthorityType = ReadAuthorityType(rawText) ?? ReadOption(tokens, "类型="),
            BonusPenalty = ReadOption(tokens, "奖惩=")
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

    private static DiceCommand ParseBlessing(string rawText, IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 3)
        {
            return Invalid(rawText, "加护检定格式应为：加护 <角色> <属性>");
        }

        return new DiceCommand
        {
            RawText = rawText,
            Kind = DiceCommandKind.Blessing,
            RollerName = tokens[1],
            AttributeName = tokens[2],
            CountersAuthority = ReadYesNoOption(tokens, "对抗权能=") ?? false,
            BonusPenalty = ReadOption(tokens, "奖惩=")
        };
    }

    private static string? ReadOption(IEnumerable<string> tokens, string prefix)
    {
        var token = tokens.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal));
        return token is null ? null : token[prefix.Length..];
    }

    private static bool? ReadYesNoOption(IEnumerable<string> tokens, string prefix)
    {
        return ReadOption(tokens, prefix) switch
        {
            "是" => true,
            "否" => false,
            _ => null
        };
    }

    private static string? ReadAuthorityType(string rawText)
    {
        var match = AuthorityTypeRegex().Match(rawText);
        return match.Success ? match.Groups["type"].Value.Trim() : null;
    }

    private static bool IsLegacyToken(string token)
    {
        return token.Equals(".r1d100", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("平局=", StringComparison.Ordinal);
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

    [GeneratedRegex("判定\\s*[:：]\\s*(?<command>[^\\r\\n。；;]+)", RegexOptions.Compiled)]
    private static partial Regex JudgementRegex();

    [GeneratedRegex("类型=(?<type>死亡回归|Invisible Providence|Cor Leonis|狮子的心脏)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex AuthorityTypeRegex();
}

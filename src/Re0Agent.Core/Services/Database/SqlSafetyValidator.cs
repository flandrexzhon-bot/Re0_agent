using System.Text.RegularExpressions;

namespace Re0Agent.Core.Services.Database;

public sealed partial class SqlSafetyValidator
{
    private static readonly HashSet<string> AllowedTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "global_state",
        "world_map_points",
        "map_elements",
        "factions",
        "protagonist_info",
        "important_npc",
        "inventory",
        "equipment",
        "quests",
        "chronicle",
        "character_memory"
    };

    public SqlValidationResult ValidateBatch(IEnumerable<string> statements)
    {
        foreach (var statement in statements)
        {
            var result = ValidateStatement(statement);
            if (!result.IsValid)
            {
                return result;
            }
        }

        return SqlValidationResult.Valid;
    }

    public SqlValidationResult ValidateStatement(string statement)
    {
        if (string.IsNullOrWhiteSpace(statement))
        {
            return SqlValidationResult.Invalid("SQL statement is empty.");
        }

        var normalized = statement.Trim();

        // 注意：以下结构检查必须忽略单引号字符串字面量里的内容，
        // 否则像 base_attributes='体质:95; 敏捷:98' 这种含分号的合法值会被误判为多语句。
        var outsideStrings = StripStringLiterals(normalized);

        if (outsideStrings.Contains("--", StringComparison.Ordinal) || outsideStrings.Contains("/*", StringComparison.Ordinal))
        {
            return SqlValidationResult.Invalid("SQL comments are not allowed.");
        }

        if (HasMultipleStatements(outsideStrings))
        {
            return SqlValidationResult.Invalid("Only one SQL statement is allowed per item.");
        }

        var table = TryGetTargetTable(normalized, out var command);
        if (table is null)
        {
            return SqlValidationResult.Invalid("Only INSERT and UPDATE statements are allowed.");
        }

        if (!AllowedTables.Contains(table))
        {
            return SqlValidationResult.Invalid($"Table '{table}' is not allowed.");
        }

        if (!string.Equals(command, "INSERT", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(command, "UPDATE", StringComparison.OrdinalIgnoreCase))
        {
            return SqlValidationResult.Invalid($"Command '{command}' is not allowed.");
        }

        return SqlValidationResult.Valid;
    }

    private static bool HasMultipleStatements(string statement)
    {
        var trimmed = statement.TrimEnd();
        var firstSemicolon = trimmed.IndexOf(';');
        if (firstSemicolon < 0)
        {
            return false;
        }

        // 允许末尾单个分号；分号出现在中间或出现多次则视为多语句。
        return firstSemicolon != trimmed.Length - 1;
    }

    /// <summary>
    /// 把单引号字符串字面量内容替换为等长空格，便于对"语句结构"做检查
    /// 而不被字面量里的分号/双连字符干扰。SQLite 以连续两个单引号 '' 转义引号。
    /// </summary>
    private static string StripStringLiterals(string sql)
    {
        var chars = sql.ToCharArray();
        var inString = false;
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] == '\'')
            {
                if (inString && i + 1 < chars.Length && chars[i + 1] == '\'')
                {
                    // 转义的引号 ''，两个字符都置空并跳过。
                    chars[i] = ' ';
                    chars[i + 1] = ' ';
                    i++;
                    continue;
                }
                inString = !inString;
                continue;
            }

            if (inString)
            {
                chars[i] = ' ';
            }
        }

        return new string(chars);
    }

    private static string? TryGetTargetTable(string statement, out string command)
    {
        var insert = InsertTargetRegex().Match(statement);
        if (insert.Success)
        {
            command = "INSERT";
            return insert.Groups["table"].Value;
        }

        var update = UpdateTargetRegex().Match(statement);
        if (update.Success)
        {
            command = "UPDATE";
            return update.Groups["table"].Value;
        }

        command = string.Empty;
        return null;
    }

    [GeneratedRegex(@"^\s*INSERT\s+INTO\s+(?<table>[A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InsertTargetRegex();

    [GeneratedRegex(@"^\s*UPDATE\s+(?<table>[A-Za-z_][A-Za-z0-9_]*)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UpdateTargetRegex();
}

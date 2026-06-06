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
        if (normalized.Contains("--", StringComparison.Ordinal) || normalized.Contains("/*", StringComparison.Ordinal))
        {
            return SqlValidationResult.Invalid("SQL comments are not allowed.");
        }

        if (HasMultipleStatements(normalized))
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
        var firstSemicolon = statement.IndexOf(';');
        if (firstSemicolon < 0)
        {
            return false;
        }

        return firstSemicolon != statement.Length - 1
            || statement[..^1].Contains(';', StringComparison.Ordinal);
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

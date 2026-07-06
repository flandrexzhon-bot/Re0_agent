using System.Text;
using System.Text.RegularExpressions;

namespace Re0Agent.Core.Services.Database;

public sealed partial class FormAgentSqlExecutor
{
    private static string LimitConstrainedTextFields(string statement)
    {
        var insert = InsertValuesRegex().Match(statement);
        if (insert.Success)
        {
            var tableName = insert.Groups["table"].Value;
            var columns = SplitTopLevel(insert.Groups["columns"].Value, ',')
                .Select(NormalizeIdentifier)
                .ToList();

            if (columns.Count == 0)
            {
                return statement;
            }

            var valuesGroup = insert.Groups["values"];
            var rewrittenValues = RewriteInsertValues(tableName, columns, valuesGroup.Value);
            return string.Equals(rewrittenValues, valuesGroup.Value, StringComparison.Ordinal)
                ? statement
                : statement[..valuesGroup.Index]
                    + rewrittenValues
                    + statement[(valuesGroup.Index + valuesGroup.Length)..];
        }

        var update = UpdateStartRegex().Match(statement);
        if (!update.Success)
        {
            return statement;
        }

        var assignmentsStart = update.Index + update.Length;
        var assignmentsEnd = FindKeywordOutsideStrings(statement, "WHERE", assignmentsStart);
        if (assignmentsEnd < 0)
        {
            assignmentsEnd = FindStatementEnd(statement);
        }

        var assignments = statement[assignmentsStart..assignmentsEnd];
        var rewrittenAssignments = RewriteAssignments(update.Groups["table"].Value, assignments);
        return string.Equals(rewrittenAssignments, assignments, StringComparison.Ordinal)
            ? statement
            : statement[..assignmentsStart] + rewrittenAssignments + statement[assignmentsEnd..];
    }

    private static string RewriteInsertValues(
        string tableName,
        IReadOnlyList<string> columns,
        string valuesText)
    {
        if (!TrySplitValueGroups(valuesText, out var groups))
        {
            return valuesText;
        }

        var changed = false;
        var rewrittenGroups = new List<string>(groups.Count);
        foreach (var group in groups)
        {
            var trimmedGroup = group.Trim();
            if (trimmedGroup.Length < 2 || trimmedGroup[0] != '(' || trimmedGroup[^1] != ')')
            {
                return valuesText;
            }

            var values = SplitTopLevel(trimmedGroup[1..^1], ',');
            if (values.Count != columns.Count)
            {
                return valuesText;
            }

            for (var i = 0; i < values.Count; i++)
            {
                if (GameStateTextNormalizer.TryGetMaxLength(tableName, columns[i], out var maxLength)
                    && TryLimitStringLiteral(values[i], maxLength, out var rewrittenValue))
                {
                    values[i] = rewrittenValue;
                    changed = true;
                }
            }

            rewrittenGroups.Add("(" + string.Join(", ", values) + ")");
        }

        return changed ? string.Join(", ", rewrittenGroups) : valuesText;
    }

    private static string RewriteAssignments(string tableName, string assignmentsText)
    {
        var assignments = SplitTopLevel(assignmentsText, ',');
        var changed = false;

        for (var i = 0; i < assignments.Count; i++)
        {
            var assignment = assignments[i];
            var equalsIndex = FindTopLevelChar(assignment, '=');
            if (equalsIndex < 0)
            {
                continue;
            }

            var columnName = NormalizeIdentifier(assignment[..equalsIndex]);
            var value = assignment[(equalsIndex + 1)..];
            if (!GameStateTextNormalizer.TryGetMaxLength(tableName, columnName, out var maxLength)
                || !TryLimitStringLiteral(value, maxLength, out var rewrittenValue))
            {
                continue;
            }

            assignments[i] = assignment[..(equalsIndex + 1)] + rewrittenValue;
            changed = true;
        }

        return changed ? string.Join(", ", assignments) : assignmentsText;
    }

    private static bool TryLimitStringLiteral(string token, int maxLength, out string rewritten)
    {
        rewritten = token;

        var leadingLength = token.Length - token.TrimStart().Length;
        var trailingLength = token.Length - token.TrimEnd().Length;
        var trimmed = token.Trim();
        if (!TryParseSingleQuotedLiteral(trimmed, out var value))
        {
            return false;
        }

        var limited = GameStateTextNormalizer.LimitText(value.Trim(), maxLength);
        if (string.Equals(limited, value, StringComparison.Ordinal))
        {
            return false;
        }

        rewritten = token[..leadingLength]
            + "'"
            + EscapeSqlString(limited)
            + "'"
            + token[(token.Length - trailingLength)..];
        return true;
    }

    private static bool TryParseSingleQuotedLiteral(string token, out string value)
    {
        value = string.Empty;
        if (token.Length < 2 || token[0] != '\'')
        {
            return false;
        }

        var builder = new StringBuilder();
        for (var i = 1; i < token.Length; i++)
        {
            if (token[i] != '\'')
            {
                builder.Append(token[i]);
                continue;
            }

            if (i + 1 < token.Length && token[i + 1] == '\'')
            {
                builder.Append('\'');
                i++;
                continue;
            }

            if (i != token.Length - 1)
            {
                return false;
            }

            value = builder.ToString();
            return true;
        }

        return false;
    }

    private static bool TrySplitValueGroups(string valuesText, out List<string> groups)
    {
        groups = new List<string>();
        var index = 0;

        while (true)
        {
            SkipWhitespace(valuesText, ref index);
            if (index >= valuesText.Length)
            {
                return groups.Count > 0;
            }

            if (groups.Count > 0)
            {
                if (valuesText[index] != ',')
                {
                    return false;
                }

                index++;
                SkipWhitespace(valuesText, ref index);
            }

            if (index >= valuesText.Length || valuesText[index] != '(')
            {
                return false;
            }

            var start = index;
            var depth = 0;
            var inString = false;
            for (; index < valuesText.Length; index++)
            {
                var current = valuesText[index];
                if (current == '\'')
                {
                    if (inString && index + 1 < valuesText.Length && valuesText[index + 1] == '\'')
                    {
                        index++;
                        continue;
                    }

                    inString = !inString;
                    continue;
                }

                if (inString)
                {
                    continue;
                }

                if (current == '(')
                {
                    depth++;
                }
                else if (current == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        index++;
                        groups.Add(valuesText[start..index]);
                        break;
                    }

                    if (depth < 0)
                    {
                        return false;
                    }
                }
            }

            if (depth != 0)
            {
                return false;
            }
        }
    }

    private static List<string> SplitTopLevel(string text, char delimiter)
    {
        var items = new List<string>();
        var start = 0;
        var depth = 0;
        var inString = false;

        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];
            if (current == '\'')
            {
                if (inString && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (current == '(')
            {
                depth++;
            }
            else if (current == ')' && depth > 0)
            {
                depth--;
            }
            else if (current == delimiter && depth == 0)
            {
                items.Add(text[start..i].Trim());
                start = i + 1;
            }
        }

        items.Add(text[start..].Trim());
        return items;
    }

    private static int FindTopLevelChar(string text, char target)
    {
        var depth = 0;
        var inString = false;

        for (var i = 0; i < text.Length; i++)
        {
            var current = text[i];
            if (current == '\'')
            {
                if (inString && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (current == '(')
            {
                depth++;
            }
            else if (current == ')' && depth > 0)
            {
                depth--;
            }
            else if (current == target && depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindKeywordOutsideStrings(string sql, string keyword, int start)
    {
        var depth = 0;
        var inString = false;

        for (var i = start; i <= sql.Length - keyword.Length; i++)
        {
            var current = sql[i];
            if (current == '\'')
            {
                if (inString && i + 1 < sql.Length && sql[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                inString = !inString;
                continue;
            }

            if (inString)
            {
                continue;
            }

            if (current == '(')
            {
                depth++;
                continue;
            }

            if (current == ')' && depth > 0)
            {
                depth--;
                continue;
            }

            if (depth == 0 && IsKeywordAt(sql, keyword, i))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsKeywordAt(string sql, string keyword, int index)
    {
        if (!sql.AsSpan(index, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var beforeOk = index == 0 || !IsIdentifierChar(sql[index - 1]);
        var afterIndex = index + keyword.Length;
        var afterOk = afterIndex >= sql.Length || !IsIdentifierChar(sql[afterIndex]);
        return beforeOk && afterOk;
    }

    private static int FindStatementEnd(string statement)
    {
        var end = statement.Length;
        while (end > 0 && char.IsWhiteSpace(statement[end - 1]))
        {
            end--;
        }

        return end > 0 && statement[end - 1] == ';' ? end - 1 : end;
    }

    private static string NormalizeIdentifier(string identifier)
    {
        var normalized = identifier.Trim();
        var dotIndex = normalized.LastIndexOf('.');
        if (dotIndex >= 0)
        {
            normalized = normalized[(dotIndex + 1)..].Trim();
        }

        return normalized.Length >= 2 && IsQuotedIdentifier(normalized)
            ? normalized[1..^1]
            : normalized;
    }

    private static bool IsQuotedIdentifier(string identifier)
    {
        return (identifier[0] == '"' && identifier[^1] == '"')
            || (identifier[0] == '`' && identifier[^1] == '`')
            || (identifier[0] == '[' && identifier[^1] == ']');
    }

    private static bool IsIdentifierChar(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_';
    }

    private static void SkipWhitespace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }
    }

    private static string EscapeSqlString(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^\s*INSERT\s+(?:OR\s+(?:IGNORE|REPLACE)\s+)?INTO\s+(?<table>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<columns>[^)]*)\)\s+VALUES\s*(?<values>.*?)(?:;)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex InsertValuesRegex();

    [GeneratedRegex(@"^\s*UPDATE\s+(?<table>[A-Za-z_][A-Za-z0-9_]*)\s+SET\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UpdateStartRegex();
}

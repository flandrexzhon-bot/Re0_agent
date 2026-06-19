using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Llm;

namespace Re0Agent.Core.Services.Agent;

public sealed partial class FormAgent(
    Re0AgentDbContext dbContext,
    AgentConfigResolver configResolver,
    PromptComposer promptComposer,
    ILlmClient llmClient)
{
    public async Task<IReadOnlyList<string>> GenerateSqlAsync(
        GameRound round,
        CancellationToken cancellationToken = default)
    {
        var config = await configResolver.FindConfigAsync("Form", "填表Agent", cancellationToken);

        var response = await llmClient.SendChatAsync(
            new LlmRequest
            {
                AgentName = "填表Agent",
                Options = AgentConfigResolver.ToLlmOptions(config),
                Messages =
                [
                    LlmMessage.System(config?.SystemPrompt ?? "你是填表Agent，按 <tableEdit> 格式输出 SQL。"),
                    LlmMessage.User(promptComposer.ComposeFormAgent(round, await CreateDatabaseSummaryAsync(cancellationToken)))
                ]
            },
            cancellationToken);

        return ParseSqlPayload(response.Content);
    }

    private async Task<string> CreateDatabaseSummaryAsync(CancellationToken cancellationToken)
    {
        var global = await dbContext.GlobalStates.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var npcNames = await dbContext.ImportantNpcs.AsNoTracking()
            .OrderBy(n => n.RowId)
            .Select(n => n.Name)
            .ToListAsync(cancellationToken);
        var memoryCount = await dbContext.CharacterMemory.CountAsync(cancellationToken);

        var npcList = npcNames.Count == 0 ? "（无）" : string.Join("、", npcNames);

        return $"global={(global is null ? "none" : $"{global.CurrentLocation}/{global.CurTime}/chapter={global.CurrentChapter}")}; protagonist={protagonist?.Name ?? "none"}; 已在册NPC({npcNames.Count}个)=[{npcList}]; memory_count={memoryCount}";
    }

    private static IReadOnlyList<string> ParseSqlPayload(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        // 首选：抽取 <tableEdit>...</tableEdit> 块里的 SQL（新格式）。
        var match = TableEditRegex().Match(content);
        if (match.Success)
        {
            return SplitStatements(match.Groups["body"].Value);
        }

        // 兜底：旧格式 {"sql":[...]} JSON。
        var trimmed = content.Trim();
        if (trimmed.Contains("\"sql\"", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var start = trimmed.IndexOf('{');
                var end = trimmed.LastIndexOf('}');
                if (start >= 0 && end > start)
                {
                    using var document = JsonDocument.Parse(trimmed[start..(end + 1)]);
                    if (document.RootElement.TryGetProperty("sql", out var sqlArray)
                        && sqlArray.ValueKind == JsonValueKind.Array)
                    {
                        return sqlArray.EnumerateArray()
                            .Select(element => element.GetString())
                            .Where(statement => !string.IsNullOrWhiteSpace(statement))
                            .Select(statement => statement!.Trim())
                            .ToList();
                    }
                }
            }
            catch (JsonException)
            {
                // 落到下面的裸文本切分。
            }
        }

        // 再兜底：把整段内容按语句切分（模型未包标签时）。
        return SplitStatements(content);
    }

    /// <summary>
    /// 按"行尾分号"切分多条 SQL，忽略单引号字符串字面量内部的分号。
    /// </summary>
    private static IReadOnlyList<string> SplitStatements(string body)
    {
        var statements = new List<string>();
        var current = new System.Text.StringBuilder();
        var inString = false;

        for (var i = 0; i < body.Length; i++)
        {
            var ch = body[i];
            current.Append(ch);

            if (ch == '\'')
            {
                if (inString && i + 1 < body.Length && body[i + 1] == '\'')
                {
                    current.Append(body[i + 1]);
                    i++;
                    continue;
                }
                inString = !inString;
                continue;
            }

            if (ch == ';' && !inString)
            {
                var stmt = current.ToString().Trim().TrimEnd(';').Trim();
                if (!string.IsNullOrWhiteSpace(stmt) && !IsCommentOnly(stmt))
                {
                    statements.Add(stmt);
                }
                current.Clear();
            }
        }

        var tail = current.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(tail) && !IsCommentOnly(tail))
        {
            statements.Add(tail);
        }

        return statements;
    }

    private static bool IsCommentOnly(string statement) =>
        statement.StartsWith("--", StringComparison.Ordinal)
        || statement.StartsWith("/*", StringComparison.Ordinal);

    [GeneratedRegex(@"<tableEdit>(?<body>[\s\S]*?)</tableEdit>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TableEditRegex();
}

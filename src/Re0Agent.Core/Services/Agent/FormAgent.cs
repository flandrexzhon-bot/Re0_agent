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
        // 数据库概览只算一次，4 个分区请求共用，避免重复查询。
        var databaseSummary = await CreateDatabaseSummaryAsync(cancellationToken);

        // 把单次大填表拆成 4 个并发子请求，各自只负责自己分区的表，互不干扰：
        //  1. 角色记忆(character_memory)
        //  2. AM 世界概括(chronicle)
        //  3. important_npc 表
        //  4. 其余所有表
        var partitions = new[]
        {
            FormTablePartition.CharacterMemory,
            FormTablePartition.Chronicle,
            FormTablePartition.ImportantNpc,
            FormTablePartition.Rest,
        };

        var tasks = partitions
            .Select(partition => GeneratePartitionSqlAsync(round, databaseSummary, config, partition, cancellationToken))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // 汇总 4 个分区的 SQL，统一交给执行器在同一事务内原子执行。
        return results.SelectMany(statements => statements).ToList();
    }

    private async Task<IReadOnlyList<string>> GeneratePartitionSqlAsync(
        GameRound round,
        string databaseSummary,
        Re0Agent.Core.Entities.AgentConfig? config,
        FormTablePartition partition,
        CancellationToken cancellationToken)
    {
        var response = await llmClient.SendChatAsync(
            new LlmRequest
            {
                AgentName = "填表Agent",
                Options = AgentConfigResolver.ToLlmOptions(config),
                Messages =
                [
                    LlmMessage.System(config?.SystemPrompt ?? "你是填表Agent，按 <tableEdit> 格式输出 SQL。"),
                    LlmMessage.User(promptComposer.ComposeFormAgent(round, databaseSummary, partition))
                ]
            },
            cancellationToken);

        return ParseSqlPayload(response.Content);
    }

    private async Task<string> CreateDatabaseSummaryAsync(CancellationToken cancellationToken)
    {
        var global = await dbContext.GlobalStates.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

        // 查询每张业务表的行数，让模型能判断哪些表为空需要初始化。
        var npcCount = await dbContext.ImportantNpcs.CountAsync(cancellationToken);
        var mapPointCount = await dbContext.WorldMapPoints.CountAsync(cancellationToken);
        var mapElementCount = await dbContext.MapElements.CountAsync(cancellationToken);
        var factionCount = await dbContext.Factions.CountAsync(cancellationToken);
        var inventoryCount = await dbContext.Inventory.CountAsync(cancellationToken);
        var equipmentCount = await dbContext.Equipment.CountAsync(cancellationToken);
        var questCount = await dbContext.Quests.CountAsync(cancellationToken);
        var chronicleCount = await dbContext.Chronicle.CountAsync(cancellationToken);
        var memoryCount = await dbContext.CharacterMemory.CountAsync(cancellationToken);

        var npcNames = npcCount == 0
            ? "（无）"
            : string.Join("、", await dbContext.ImportantNpcs.AsNoTracking()
                .OrderBy(n => n.RowId)
                .Select(n => n.Name)
                .ToListAsync(cancellationToken));

        return $"global={(global is null ? "【空表-需要初始化】" : $"{global.CurrentLocation}/{global.CurTime}/chapter={global.CurrentChapter}")}; "
            + $"protagonist={(protagonist is null ? "【空表-需要初始化】" : $"{protagonist.Name}/{protagonist.LocationName}")}; "
            + $"world_map_points={mapPointCount}行; map_elements={mapElementCount}行; factions={factionCount}行; "
            + $"important_npc={npcCount}行=[{npcNames}]; "
            + $"inventory={inventoryCount}行; equipment={equipmentCount}行; quests={questCount}行; "
            + $"chronicle={chronicleCount}行; character_memory={memoryCount}行";
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

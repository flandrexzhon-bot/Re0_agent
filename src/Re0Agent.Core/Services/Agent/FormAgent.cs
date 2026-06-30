using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Database;
using Re0Agent.Core.Services.Llm;
using Re0Agent.Core.Services.Settings;

namespace Re0Agent.Core.Services.Agent;

public sealed partial class FormAgent(
    Re0AgentDbContext dbContext,
    AgentConfigResolver configResolver,
    PromptComposer promptComposer,
    ILlmClient llmClient,
    IRagService ragService,
    FormAgentSqlExecutor sqlExecutor)
{
    /// <summary>单个分区请求的最大重试次数（生成报错/空输出，或<strong>执行失败</strong>，均重试该分区，不波及其余分区）。</summary>
    private const int MaxPartitionRetries = 3;

    /// <summary>四个分区的固定顺序（执行需串行，因共用同一个 DbContext/连接）。</summary>
    private static readonly FormTablePartition[] AllPartitions =
    [
        FormTablePartition.CharacterMemory,
        FormTablePartition.Chronicle,
        FormTablePartition.ImportantNpc,
        FormTablePartition.Rest,
    ];

    /// <summary>一个分区填表请求的结果：成功则带 SQL，失败则带最终错误信息。</summary>
    public sealed record PartitionResult(
        FormTablePartition Partition,
        IReadOnlyList<string> Sql,
        string? ErrorMessage);

    /// <summary>
    /// 一个分区<strong>从生成到执行落库</strong>的最终结果：
    /// <paramref name="StatementsExecuted"/> 为该分区已提交的语句数；
    /// <paramref name="ErrorMessage"/> 非空表示该分区（生成或执行）重试用尽仍失败、未落库。
    /// </summary>
    public sealed record PartitionExecution(
        FormTablePartition Partition,
        int StatementsExecuted,
        string? ErrorMessage);

    /// <summary>四个分区生成共用的上下文（只算一次）。</summary>
    private sealed record SharedInputs(
        Re0Agent.Core.Entities.AgentConfig? Config,
        string DatabaseSummary,
        RagContext RagContext);

    public static string DescribePartition(FormTablePartition partition) => partition switch
    {
        FormTablePartition.CharacterMemory => "角色记忆",
        FormTablePartition.Chronicle => "AM世界概括",
        FormTablePartition.ImportantNpc => "重要NPC",
        FormTablePartition.Rest => "其余表",
        _ => partition.ToString()
    };

    public async Task<IReadOnlyList<string>> GenerateSqlAsync(
        GameRound round,
        CancellationToken cancellationToken = default)
    {
        var result = await GeneratePartitionedAsync(round, cancellationToken);
        return result.SelectMany(r => r.Sql).ToList();
    }

    /// <summary>
    /// 把单次大填表拆成 4 个并发子请求，各自只负责自己分区的表，互不干扰：
    ///  1. 角色记忆(character_memory) 2. AM 世界概括(chronicle) 3. important_npc 表 4. 其余所有表。
    /// <para>每个分区<strong>独立重试</strong>（最多 <see cref="MaxPartitionRetries"/> 次）——某个分区报错
    /// 只重发该分区的请求，不影响、也不重发其余 3 个分区。</para>
    /// 返回每个分区的结果（含失败信息），由调用方汇总 SQL 并记录失败事件。
    /// </summary>
    public async Task<IReadOnlyList<PartitionResult>> GeneratePartitionedAsync(
        GameRound round,
        CancellationToken cancellationToken = default)
    {
        var shared = await BuildSharedInputsAsync(round, cancellationToken);

        var tasks = AllPartitions
            .Select(partition => GeneratePartitionSqlAsync(round, shared.DatabaseSummary, shared.RagContext, shared.Config, partition, cancellationToken))
            .ToArray();

        return await Task.WhenAll(tasks);
    }

    /// <summary>
    /// 完整填表：4 个分区<strong>并发生成</strong> → <strong>逐分区独立执行落库</strong>。
    /// <para>每个分区在<em>自己的事务</em>里提交：成功的分区直接写入；执行失败（如 SQL 校验拒绝、
    /// 类型不符）的分区<strong>重新生成并重试</strong>（最多 <see cref="MaxPartitionRetries"/> 次），
    /// 全程<strong>不回滚、不波及其余分区</strong>。某分区彻底失败只丢该分区，其余照常落库。</para>
    /// 因 4 个分区共用同一个 DbContext/SQLite 连接，执行阶段必须<strong>串行</strong>。
    /// </summary>
    public async Task<IReadOnlyList<PartitionExecution>> FillAndExecuteAsync(
        GameRound round,
        CancellationToken cancellationToken = default)
    {
        var shared = await BuildSharedInputsAsync(round, cancellationToken);

        // 1. 并发生成各分区 SQL（生成阶段只调 LLM，不碰 DbContext，可并行）。
        var generated = await Task.WhenAll(AllPartitions
            .Select(partition => GeneratePartitionSqlAsync(round, shared.DatabaseSummary, shared.RagContext, shared.Config, partition, cancellationToken)));

        // 2. 逐分区独立执行（共用连接 → 串行）；失败分区重新生成 + 重试，各自独立提交。
        var results = new List<PartitionExecution>(generated.Length);
        foreach (var gen in generated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ExecutePartitionWithRetryAsync(round, shared, gen, cancellationToken));
        }
        return results;
    }

    private async Task<SharedInputs> BuildSharedInputsAsync(GameRound round, CancellationToken cancellationToken)
    {
        var config = await configResolver.FindConfigAsync("Form", "填表Agent", cancellationToken);
        // 数据库概览只算一次，4 个分区请求共用，避免重复查询。
        var databaseSummary = await CreateDatabaseSummaryAsync(cancellationToken);
        // 世界书检索也只算一次：用本回合正文（开场+各角色行动+玩家输入）作关键词，
        // 命中如「爱蜜莉雅」等设定条目，喂给填表 Agent 作 <背景设定>，避免它凭空臆造人物/地点设定。
        var ragContext = await QueryWorldBookAsync(round, cancellationToken);
        return new SharedInputs(config, databaseSummary, ragContext);
    }

    /// <summary>
    /// 单分区「执行落库」循环：用已生成的 SQL 尝试执行；执行失败则<strong>重新生成该分区</strong>后重试，
    /// 直到成功或 <see cref="MaxPartitionRetries"/> 次用尽。空 SQL 且无错误视为「模型判断无需更新」=成功。
    /// </summary>
    private async Task<PartitionExecution> ExecutePartitionWithRetryAsync(
        GameRound round,
        SharedInputs shared,
        PartitionResult generated,
        CancellationToken cancellationToken)
    {
        var partition = generated.Partition;
        var sql = generated.Sql;
        var lastError = generated.ErrorMessage;

        for (var attempt = 1; attempt <= MaxPartitionRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (sql.Count > 0)
            {
                try
                {
                    var exec = await sqlExecutor.ExecuteAsync(sql, cancellationToken);
                    return new PartitionExecution(partition, exec.StatementsExecuted, ErrorMessage: null);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 该分区整批执行失败（已自动回滚其事务，未污染其余分区）。
                    lastError = ex.Message;
                }
            }
            else if (lastError is null)
            {
                // 模型判断该分区无需更新：无 SQL 即成功。
                return new PartitionExecution(partition, 0, ErrorMessage: null);
            }

            // 执行失败，或生成阶段就失败（空+错误）。还有重试机会则重新生成该分区。
            if (attempt < MaxPartitionRetries)
            {
                var regen = await GeneratePartitionSqlAsync(round, shared.DatabaseSummary, shared.RagContext, shared.Config, partition, cancellationToken);
                sql = regen.Sql;
                lastError = regen.ErrorMessage;
            }
        }

        return new PartitionExecution(partition, 0, lastError ?? "未知错误");
    }

    /// <summary>
    /// 用本回合正文构造关键词检索世界书。不限制分类（AllowedCategories=null），
    /// 让人物/地点/势力等条目都能按关键词命中——正文里提到「爱蜜莉雅」就会拉到她的设定。
    /// </summary>
    private async Task<RagContext> QueryWorldBookAsync(GameRound round, CancellationToken cancellationToken)
    {
        var turnText = string.Join('\n', round.CharacterTurns.Select(turn =>
            $"{turn.CharacterName} {turn.ActionText} {turn.GmJudgement} {turn.ResultResponse}"));

        var queryText = string.Join('\n', new[]
        {
            round.PlayerInput,
            round.GmOpening,
            turnText
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        if (string.IsNullOrWhiteSpace(queryText))
        {
            return new RagContext();
        }

        return await ragService.QueryAsync(
            new RagQuery
            {
                Text = queryText,
                Chapter = round.Chapter,
                MaxNonConstantEntries = 12
            },
            cancellationToken);
    }

    private async Task<PartitionResult> GeneratePartitionSqlAsync(
        GameRound round,
        string databaseSummary,
        RagContext ragContext,
        Re0Agent.Core.Entities.AgentConfig? config,
        FormTablePartition partition,
        CancellationToken cancellationToken)
    {
        string? lastError = null;

        for (var attempt = 1; attempt <= MaxPartitionRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await llmClient.SendChatAsync(
                    new LlmRequest
                    {
                        AgentName = "填表Agent",
                        Options = AgentConfigResolver.ToLlmOptions(config),
                        Messages =
                        [
                            LlmMessage.System(config?.SystemPrompt ?? "你是填表Agent，按 <tableEdit> 格式输出 SQL。"),
                            LlmMessage.User(promptComposer.ComposeFormAgent(round, databaseSummary, partition, ragContext: ragContext))
                        ]
                    },
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(response.ErrorMessage))
                {
                    lastError = response.ErrorMessage;
                    continue;
                }

                var sql = ParseSqlPayload(response.Content);
                return new PartitionResult(partition, sql, ErrorMessage: null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }

        // 该分区重试用尽仍失败：返回空 SQL + 错误信息，不抛出，保证其余分区照常落库。
        return new PartitionResult(partition, [], lastError ?? "未知错误");
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

using System.Text;
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
        // 填表 Agent 必须看到【每一行的全部业务字段】才能正确 UPDATE（按 UNIQUE 键定位、只改变化列）
        // 以及正确判空初始化。故这里逐表 dump 全字段，并在每张表标注行数。
        var sb = new StringBuilder();

        var global = await dbContext.GlobalStates.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        sb.AppendLine(global is null
            ? "【global_state】0行 —— 空表，需要初始化。"
            : "【global_state】1行：\n" + $"  row_id={global.RowId}; current_location={global.CurrentLocation}; current_minor_region={global.CurrentMinorRegion}; current_major_region={global.CurrentMajorRegion}; prev_scene_time={global.PrevSceneTime ?? "NULL"}; elapsed_time={global.ElapsedTime}; cur_time={global.CurTime}; current_chapter={global.CurrentChapter}; is_lewd={global.IsLewd}");

        var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        sb.AppendLine(protagonist is null
            ? "【protagonist_info】0行 —— 空表，需要初始化。"
            : "【protagonist_info】1行：\n" + $"  row_id={protagonist.RowId}; char_id={protagonist.CharId}; name={protagonist.Name}; gender={protagonist.Gender}; age={protagonist.Age}; location_name={protagonist.LocationName}; self_status={protagonist.SelfStatus}; base_attributes={protagonist.BaseAttributes}; special_attributes={protagonist.SpecialAttributes ?? "NULL"}; resources_text={protagonist.ResourcesText ?? "NULL"}; hp={protagonist.Hp}/{protagonist.MaxHp}; mp={protagonist.Mp}/{protagonist.MaxMp}; stamina={protagonist.Stamina}/{protagonist.MaxStamina}; armor={protagonist.Armor}; identity_text={protagonist.IdentityText}; appearance={protagonist.Appearance}; skills_json={protagonist.SkillsJson ?? "NULL"}");

        var npcs = await dbContext.ImportantNpcs.AsNoTracking().OrderBy(n => n.RowId).ToListAsync(cancellationToken);
        AppendTable(sb, "important_npc", npcs, n =>
            $"row_id={n.RowId}; char_id={n.CharId}; name={n.Name}; gender={n.Gender}; age={n.Age}; location_name={n.LocationName}; self_status={n.SelfStatus}; base_attributes={n.BaseAttributes}; special_attributes={n.SpecialAttributes ?? "NULL"}; hp={n.Hp}/{n.MaxHp}; mp={n.Mp}/{n.MaxMp}; stamina={n.Stamina}/{n.MaxStamina}; armor={n.Armor}; brief_intro={n.BriefIntro}; identity_text={n.IdentityText}; relations_text={n.RelationsText ?? "NULL"}; interaction_options={n.InteractionOptions ?? "NULL"}; skills_json={n.SkillsJson ?? "NULL"}");

        var mapPoints = await dbContext.WorldMapPoints.AsNoTracking().OrderBy(p => p.RowId).ToListAsync(cancellationToken);
        AppendTable(sb, "world_map_points", mapPoints, p =>
            $"row_id={p.RowId}; location_name={p.LocationName}; minor_region={p.MinorRegion}; major_region={p.MajorRegion}; location_type={p.LocationType}; importance={p.Importance}; exploration_status={p.ExplorationStatus}; environment_desc={p.EnvironmentDesc}");

        var mapElements = await dbContext.MapElements.AsNoTracking().OrderBy(e => e.RowId).ToListAsync(cancellationToken);
        AppendTable(sb, "map_elements", mapElements, e =>
            $"row_id={e.RowId}; element_name={e.ElementName}; element_type={e.ElementType}; location_name={e.LocationName}; status_text={e.StatusText}; interaction_options={e.InteractionOptions}; element_desc={e.ElementDesc}");

        var factions = await dbContext.Factions.AsNoTracking().OrderBy(f => f.RowId).ToListAsync(cancellationToken);
        AppendTable(sb, "factions", factions, f =>
            $"row_id={f.RowId}; faction_name={f.FactionName}; leader={f.Leader ?? "NULL"}; headquarters={f.Headquarters ?? "NULL"}; relations_text={f.RelationsText ?? "NULL"}; description={f.Description}");

        var inventory = await dbContext.Inventory.AsNoTracking().OrderBy(i => i.RowId).ToListAsync(cancellationToken);
        AppendTable(sb, "inventory", inventory, i =>
            $"row_id={i.RowId}; item_name={i.ItemName}; item_type={i.ItemType}; quantity={i.Quantity}; quality={i.Quality}; description={i.Description}");

        var equipment = await dbContext.Equipment.AsNoTracking().OrderBy(e => e.RowId).ToListAsync(cancellationToken);
        AppendTable(sb, "equipment", equipment, e =>
            $"row_id={e.RowId}; equipment_name={e.EquipmentName}; equipment_type={e.EquipmentType}; quality={e.Quality}; status_text={e.StatusText}; description={e.Description}");

        var quests = await dbContext.Quests.AsNoTracking().OrderBy(q => q.RowId).ToListAsync(cancellationToken);
        AppendTable(sb, "quests", quests, q =>
            $"row_id={q.RowId}; quest_name={q.QuestName}; quest_type={q.QuestType}; priority_level={q.PriorityLevel}; status_tag={q.StatusTag}; progress_text={q.ProgressText}; target_desc={q.TargetDesc}; source_text={q.SourceText ?? "NULL"}; reward_text={q.RewardText ?? "NULL"}");

        // chronicle / character_memory 为追加式历史，条数可能很多：给行数 + 最近数条摘要即可，
        // 全文历史由「历史上下文深度注入」单独负责，避免填表上下文爆炸。
        var chronicleCount = await dbContext.Chronicle.CountAsync(cancellationToken);
        var recentChronicle = await dbContext.Chronicle.AsNoTracking()
            .OrderByDescending(c => c.RowId).Take(3)
            .Select(c => $"    {c.CodeIndex}({c.TimeSpan}): {c.Summary}")
            .ToListAsync(cancellationToken);
        sb.AppendLine($"【chronicle】{chronicleCount}行（追加式，仅列最近3条概览）："
            + (recentChronicle.Count == 0 ? " 空表" : "\n" + string.Join("\n", ((IEnumerable<string>)recentChronicle).Reverse())));

        var memoryCount = await dbContext.CharacterMemory.CountAsync(cancellationToken);
        var recentMemory = await dbContext.CharacterMemory.AsNoTracking()
            .OrderByDescending(m => m.RowId).Take(5)
            .Select(m => $"    [{m.RoundIndex}]{m.CharacterName}: {m.MemoryText}（情绪:{m.EmotionalState}）")
            .ToListAsync(cancellationToken);
        sb.AppendLine($"【character_memory】{memoryCount}行（追加式，仅列最近5条）："
            + (recentMemory.Count == 0 ? " 空表" : "\n" + string.Join("\n", ((IEnumerable<string>)recentMemory).Reverse())));

        return sb.ToString().TrimEnd();
    }

    /// <summary>逐行 dump 一张表：标注行数，空表明确提示需初始化，非空表每行一条全字段记录。</summary>
    private static void AppendTable<T>(StringBuilder sb, string tableName, IReadOnlyList<T> rows, Func<T, string> render)
    {
        if (rows.Count == 0)
        {
            sb.AppendLine($"【{tableName}】0行 —— 空表，若为开局需初始化。");
            return;
        }

        sb.AppendLine($"【{tableName}】{rows.Count}行：");
        foreach (var row in rows)
        {
            sb.Append("  ").AppendLine(render(row));
        }
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

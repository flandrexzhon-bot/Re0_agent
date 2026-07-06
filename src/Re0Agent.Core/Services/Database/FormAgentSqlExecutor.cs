using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Re0Agent.Core.Database;

namespace Re0Agent.Core.Services.Database;

public sealed partial class FormAgentSqlExecutor(
    Re0AgentDbContext dbContext,
    SqlSafetyValidator validator)
{
    public async Task<SqlExecutionResult> ExecuteAsync(
        IReadOnlyList<string> statements,
        CancellationToken cancellationToken = default)
    {
        var validation = validator.ValidateBatch(statements);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(validation.ErrorMessage);
        }

        // 防御：把对 important_npc 的裸 INSERT 改写为 INSERT OR IGNORE，
        // 避免角色因唯一约束(name)冲突导致整批回滚（应只更新而误插入的兜底）。
        // 同时剥离填表 Agent 对 global_state.current_chapter 的写入：章节号由章节切换
        // Agent 专属维护且必须为整数，模型若误写"第七章：xxx"会导致 INTEGER 列解析失败。
        var safeStatements = statements
            .Select(HardenImportantNpcInsert)
            .Select(StripCurrentChapterAssignment)
            .Select(LimitConstrainedTextFields)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var connection = dbContext.Database.GetDbConnection();
        foreach (var statement in safeStatements)
        {
            // 用原生 DbCommand 执行：EF 的 ExecuteSqlRawAsync 会把 SQL 里的 '{' 当作
            // {0} 参数占位符解析，而 skills_json 等 JSON 值含 '{' 会触发
            // "Expected an ASCII digit" 解析错误。原生命令按字面执行，规避该问题。
            await using var command = connection.CreateCommand();
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new SqlExecutionResult(safeStatements.Count, safeStatements);
    }

    private static string HardenImportantNpcInsert(string statement)
    {
        return BareImportantNpcInsertRegex().Replace(
            statement,
            "INSERT OR IGNORE INTO important_npc");
    }

    /// <summary>
    /// 从 global_state 的 UPDATE 中剔除 current_chapter 赋值（章节切换 Agent 专属）。
    /// 若移除后整条 UPDATE 不再有任何 SET 项，则整条丢弃。
    /// </summary>
    private static string StripCurrentChapterAssignment(string statement)
    {
        if (!CurrentChapterAssignmentRegex().IsMatch(statement))
        {
            return statement;
        }

        // 去掉 "current_chapter = <值>" 片段（含其后或其前的逗号）。
        var cleaned = CurrentChapterAssignmentRegex().Replace(statement, string.Empty);
        // 规整可能残留的 "SET ," 或 ", WHERE" 等。
        cleaned = Regex.Replace(cleaned, @"\bSET\s*,", "SET ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @",\s*WHERE", " WHERE", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ");

        // 若 SET 子句已空（SET 紧跟 WHERE），整条 UPDATE 无意义，丢弃。
        if (EmptySetUpdateRegex().IsMatch(cleaned))
        {
            return string.Empty;
        }

        return cleaned.Trim();
    }

    [GeneratedRegex(@"^\s*INSERT\s+INTO\s+important_npc\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareImportantNpcInsertRegex();

    [GeneratedRegex(@",?\s*current_chapter\s*=\s*('[^']*'|""[^""]*""|[^,\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CurrentChapterAssignmentRegex();

    [GeneratedRegex(@"\bSET\s+WHERE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmptySetUpdateRegex();
}

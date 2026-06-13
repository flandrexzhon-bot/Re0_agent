using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
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
        var safeStatements = statements
            .Select(HardenImportantNpcInsert)
            .ToList();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        foreach (var statement in safeStatements)
        {
            await dbContext.Database.ExecuteSqlRawAsync(statement, cancellationToken);
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

    [GeneratedRegex(@"^\s*INSERT\s+INTO\s+important_npc\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareImportantNpcInsertRegex();
}

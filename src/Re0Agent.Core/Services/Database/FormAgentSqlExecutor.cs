using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;

namespace Re0Agent.Core.Services.Database;

public sealed class FormAgentSqlExecutor(
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

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        foreach (var statement in statements)
        {
            await dbContext.Database.ExecuteSqlRawAsync(statement, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new SqlExecutionResult(statements.Count, statements);
    }
}

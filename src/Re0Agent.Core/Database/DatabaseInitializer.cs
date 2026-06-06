using Microsoft.EntityFrameworkCore;

namespace Re0Agent.Core.Database;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(
        Re0AgentDbContext context,
        CancellationToken cancellationToken = default)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;", cancellationToken);

            foreach (var statement in DatabaseSchema.CreateStatements)
            {
                await context.Database.ExecuteSqlRawAsync(statement, cancellationToken);
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}

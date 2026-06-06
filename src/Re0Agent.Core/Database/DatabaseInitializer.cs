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

            await EnsureSavePointUpgradeColumnsAsync(context, cancellationToken);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static async Task EnsureSavePointUpgradeColumnsAsync(
        Re0AgentDbContext context,
        CancellationToken cancellationToken)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = context.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(save_points);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        foreach (var column in DatabaseSchema.SavePointUpgradeColumns)
        {
            if (existingColumns.Contains(column.Name))
            {
                continue;
            }

            var alterStatement = "ALTER TABLE save_points ADD COLUMN "
                + column.Name
                + " "
                + column.Definition
                + ";";
            await context.Database.ExecuteSqlRawAsync(alterStatement, cancellationToken);
        }
    }
}

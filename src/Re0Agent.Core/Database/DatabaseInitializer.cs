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
            await EnsureAgentConfigUpgradeColumnsAsync(context, cancellationToken);
            await EnsureImportantNpcUpgradeColumnsAsync(context, cancellationToken);
            await EnsureChronicleUpgradeConstraintsAsync(context, cancellationToken);
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

    private static async Task EnsureAgentConfigUpgradeColumnsAsync(
        Re0AgentDbContext context,
        CancellationToken cancellationToken)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = context.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(agent_config);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        var newColumns = new[]
        {
            (Name: "max_input_tokens", Definition: "INTEGER DEFAULT 4096"),
            (Name: "response_format", Definition: "TEXT DEFAULT 'JSON'")
        };

        foreach (var column in newColumns)
        {
            if (existingColumns.Contains(column.Name))
            {
                continue;
            }

            var alterStatement = "ALTER TABLE agent_config ADD COLUMN "
                + column.Name
                + " "
                + column.Definition
                + ";";
            await context.Database.ExecuteSqlRawAsync(alterStatement, cancellationToken);
        }
    }

    private static async Task EnsureImportantNpcUpgradeColumnsAsync(
        Re0AgentDbContext context,
        CancellationToken cancellationToken)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = context.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(important_npc);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        if (!existingColumns.Contains("self_status"))
        {
            var alterStatement = "ALTER TABLE important_npc ADD COLUMN self_status TEXT NOT NULL DEFAULT '正常';";
            await context.Database.ExecuteSqlRawAsync(alterStatement, cancellationToken);
        }
    }

    private static async Task EnsureChronicleUpgradeConstraintsAsync(
        Re0AgentDbContext context,
        CancellationToken cancellationToken)
    {
        string tableSql = string.Empty;
        await using (var command = context.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='chronicle';";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is not null)
            {
                tableSql = result.ToString() ?? "";
            }
        }

        if (tableSql.Contains(">= 200 AND LENGTH(chronicle_text) <= 600", StringComparison.OrdinalIgnoreCase))
        {
            var migrationStatements = new[]
            {
                "ALTER TABLE chronicle RENAME TO chronicle_old;",
                """
                CREATE TABLE chronicle (
                  row_id INTEGER PRIMARY KEY,
                  code_index TEXT NOT NULL UNIQUE,
                  time_span TEXT NOT NULL CHECK(time_span GLOB '????-??-?? ??:?? ~ ????-??-?? ??:??'),
                  summary TEXT NOT NULL CHECK(LENGTH(summary) <= 30),
                  chronicle_text TEXT NOT NULL CHECK(LENGTH(chronicle_text) >= 100 AND LENGTH(chronicle_text) <= 1000)
                );
                """,
                "INSERT INTO chronicle (row_id, code_index, time_span, summary, chronicle_text) SELECT row_id, code_index, time_span, summary, chronicle_text FROM chronicle_old;",
                "DROP TABLE chronicle_old;"
            };

            foreach (var stmt in migrationStatements)
            {
                await context.Database.ExecuteSqlRawAsync(stmt, cancellationToken);
            }
        }
    }
}

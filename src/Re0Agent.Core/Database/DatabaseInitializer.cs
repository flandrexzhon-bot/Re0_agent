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

            await EnsureGlobalStateWorldBookTimeFormatAsync(context, cancellationToken);
            await EnsureSavePointUpgradeColumnsAsync(context, cancellationToken);
            await EnsureAgentConfigUpgradeColumnsAsync(context, cancellationToken);
            await EnsureImportantNpcUpgradeColumnsAsync(context, cancellationToken);
            await EnsureImportantNpcDropPresenceStatusAsync(context, cancellationToken);
            await EnsureChronicleUpgradeConstraintsAsync(context, cancellationToken);
            await EnsureCharacterStatColumnsAsync(context, cancellationToken);
            await EnsureChatSessionsUpgradeColumnsAsync(context, cancellationToken);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static async Task EnsureGlobalStateWorldBookTimeFormatAsync(
        Re0AgentDbContext context,
        CancellationToken cancellationToken)
    {
        string tableSql = string.Empty;
        await using (var command = context.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='global_state';";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is not null)
            {
                tableSql = result.ToString() ?? "";
            }
        }

        if (tableSql.Contains("*月-*日-*:*", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var oldColumns = await ReadColumnNamesAsync(context, "global_state", cancellationToken);
        var currentChapterSelect = oldColumns.Contains("current_chapter")
            ? "CASE WHEN current_chapter IS NOT NULL "
                + "AND CAST(current_chapter AS TEXT) GLOB '[0-9]*' "
                + "AND CAST(current_chapter AS TEXT) NOT GLOB '*[^0-9]*' "
                + "AND CAST(current_chapter AS INTEGER) >= 1 "
                + "THEN CAST(current_chapter AS INTEGER) ELSE 1 END"
            : "1";
        var isLewdSelect = oldColumns.Contains("is_lewd")
            ? "CASE WHEN is_lewd IN ('是', '否') THEN is_lewd ELSE '否' END"
            : "'否'";
        var insertStatement = $"""
            INSERT INTO global_state (
              row_id,
              current_location,
              current_minor_region,
              current_major_region,
              prev_scene_time,
              elapsed_time,
              cur_time,
              current_chapter,
              is_lewd
            )
            SELECT
              row_id,
              current_location,
              current_minor_region,
              current_major_region,
              CASE
                WHEN prev_scene_time IS NULL THEN NULL
                WHEN prev_scene_time GLOB '*月-*日-*:*' AND instr(prev_scene_time, '上午') = 0 AND instr(prev_scene_time, '下午') = 0 AND instr(prev_scene_time, '早晨') = 0 THEN prev_scene_time
                ELSE NULL
              END,
              elapsed_time,
              CASE
                WHEN cur_time GLOB '*月-*日-*:*' AND instr(cur_time, '上午') = 0 AND instr(cur_time, '下午') = 0 AND instr(cur_time, '早晨') = 0 THEN cur_time
                ELSE '未知月-未知日-??:??'
              END,
              {currentChapterSelect},
              {isLewdSelect}
            FROM global_state_old;
            """;

        var migrationStatements = new[]
        {
            "ALTER TABLE global_state RENAME TO global_state_old;",
            """
            CREATE TABLE global_state (
              row_id INTEGER PRIMARY KEY CHECK(row_id = 1),
              current_location TEXT NOT NULL,
              current_minor_region TEXT NOT NULL,
              current_major_region TEXT NOT NULL,
              prev_scene_time TEXT CHECK(prev_scene_time IS NULL OR (prev_scene_time GLOB '*月-*日-*:*' AND instr(prev_scene_time, '上午') = 0 AND instr(prev_scene_time, '下午') = 0 AND instr(prev_scene_time, '早晨') = 0)),
              elapsed_time TEXT NOT NULL,
              cur_time TEXT NOT NULL CHECK(cur_time GLOB '*月-*日-*:*' AND instr(cur_time, '上午') = 0 AND instr(cur_time, '下午') = 0 AND instr(cur_time, '早晨') = 0),
              current_chapter INTEGER NOT NULL DEFAULT 1 CHECK(current_chapter >= 1),
              is_lewd TEXT NOT NULL DEFAULT '否' CHECK(is_lewd IN ('是', '否'))
            );
            """,
            insertStatement,
            "DROP TABLE global_state_old;"
        };

        foreach (var stmt in migrationStatements)
        {
            await context.Database.ExecuteSqlRawAsync(stmt, cancellationToken);
        }
    }

    private static async Task<HashSet<string>> ReadColumnNamesAsync(
        Re0AgentDbContext context,
        string tableName,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
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
            (Name: "response_format", Definition: "TEXT DEFAULT 'JSON'"),
            (Name: "enable_thinking", Definition: "INTEGER DEFAULT 0"),
            (Name: "reasoning_effort", Definition: "TEXT DEFAULT 'medium'"),
            (Name: "auto_retry", Definition: "INTEGER DEFAULT 1")
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

    private static async Task EnsureChatSessionsUpgradeColumnsAsync(
        Re0AgentDbContext context,
        CancellationToken cancellationToken)
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = context.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(chat_sessions);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        // 分支会话父链：老库无此列时补加（SillyTavern 式 branch）。
        if (!existingColumns.Contains("parent_session_id"))
        {
            await context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE chat_sessions ADD COLUMN parent_session_id INTEGER;",
                cancellationToken);
        }

        // 各回合重 roll 变体集合：老库无此列时补加（默认空字典）。
        if (!existingColumns.Contains("round_variants_snapshot"))
        {
            await context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE chat_sessions ADD COLUMN round_variants_snapshot TEXT NOT NULL DEFAULT '{{}}';",
                cancellationToken);
        }

        // 状态机段位 + 断点段：老库无此列时补加（默认 Idle / 0）。
        if (!existingColumns.Contains("current_round_phase"))
        {
            await context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE chat_sessions ADD COLUMN current_round_phase TEXT NOT NULL DEFAULT 'Idle';",
                cancellationToken);
        }

        if (!existingColumns.Contains("interrupted_step"))
        {
            await context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE chat_sessions ADD COLUMN interrupted_step INTEGER NOT NULL DEFAULT 0;",
                cancellationToken);
        }
    }

    private static async Task EnsureCharacterStatColumnsAsync(
        Re0AgentDbContext context,
        CancellationToken cancellationToken)
    {
        var newColumns = new[]
        {
            (Name: "char_id", Definition: "INTEGER NOT NULL DEFAULT 0"),
            (Name: "hp", Definition: "INTEGER NOT NULL DEFAULT 100"),
            (Name: "max_hp", Definition: "INTEGER NOT NULL DEFAULT 100"),
            (Name: "mp", Definition: "INTEGER NOT NULL DEFAULT 0"),
            (Name: "max_mp", Definition: "INTEGER NOT NULL DEFAULT 0"),
            (Name: "stamina", Definition: "INTEGER NOT NULL DEFAULT 100"),
            (Name: "max_stamina", Definition: "INTEGER NOT NULL DEFAULT 100"),
            (Name: "armor", Definition: "INTEGER NOT NULL DEFAULT 0"),
            (Name: "skills_json", Definition: "TEXT")
        };

        foreach (var table in new[] { "protagonist_info", "important_npc" })
        {
            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var command = context.Database.GetDbConnection().CreateCommand())
            {
                command.CommandText = $"PRAGMA table_info({table});";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    existingColumns.Add(reader.GetString(1));
                }
            }

            foreach (var column in newColumns)
            {
                if (existingColumns.Contains(column.Name))
                {
                    continue;
                }

                var alterStatement = $"ALTER TABLE {table} ADD COLUMN {column.Name} {column.Definition};";
                await context.Database.ExecuteSqlRawAsync(alterStatement, cancellationToken);
            }
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

    private static async Task EnsureImportantNpcDropPresenceStatusAsync(
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

        if (!existingColumns.Contains("presence_status")) return;

        var migration = new[]
        {
            "ALTER TABLE important_npc RENAME TO important_npc_old;",
            """
            CREATE TABLE important_npc (
              row_id INTEGER PRIMARY KEY,
              name TEXT NOT NULL UNIQUE,
              gender TEXT NOT NULL,
              age INTEGER NOT NULL CHECK(age >= 0),
              brief_intro TEXT NOT NULL CHECK(LENGTH(brief_intro) <= 30),
              appearance TEXT NOT NULL CHECK(LENGTH(appearance) <= 60),
              identity_text TEXT NOT NULL CHECK(LENGTH(identity_text) <= 40),
              base_attributes TEXT NOT NULL,
              special_attributes TEXT,
              location_name TEXT NOT NULL,
              relations_text TEXT,
              interaction_options TEXT,
              past_experience TEXT NOT NULL CHECK(LENGTH(past_experience) <= 600),
              self_status TEXT NOT NULL DEFAULT '正常'
            );
            """,
            "INSERT INTO important_npc (row_id, name, gender, age, brief_intro, appearance, identity_text, base_attributes, special_attributes, location_name, relations_text, interaction_options, past_experience, self_status) SELECT row_id, name, gender, age, brief_intro, appearance, identity_text, base_attributes, special_attributes, location_name, relations_text, interaction_options, past_experience, self_status FROM important_npc_old;",
            "DROP TABLE important_npc_old;"
        };

        foreach (var stmt in migration)
        {
            await context.Database.ExecuteSqlRawAsync(stmt, cancellationToken);
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

        // 历次提高 chronicle_text 上限：旧库可能是 <=600 或 <=1000，统一迁移到 <=2000，
        // 以便前序故事/编年史容纳更多文字。
        var needsChronicleUpgrade =
            tableSql.Contains("LENGTH(chronicle_text) <= 600", StringComparison.OrdinalIgnoreCase)
            || tableSql.Contains("LENGTH(chronicle_text) <= 1000", StringComparison.OrdinalIgnoreCase);

        if (needsChronicleUpgrade)
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
                  chronicle_text TEXT NOT NULL CHECK(LENGTH(chronicle_text) >= 100 AND LENGTH(chronicle_text) <= 2000)
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

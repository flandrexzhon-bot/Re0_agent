using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Services.Settings;

namespace Re0Agent.Core.Database;

public static class DatabaseInitializer
{
    private static readonly string[] LegacyGameTables =
    [
        "death_return_log", "save_points", "character_memory", "chronicle", "quests",
        "equipment", "inventory", "important_npc", "protagonist_info", "factions",
        "map_elements", "world_map_points", "global_state", "timeline_events",
        "timeline_branches", "projection_checkpoints", "projection_entity_versions", "projection_command_log",
        "world_scheduler_jobs", "world_runtime_state", "pending_directions", "reveal_queue", "character_card_sources", "lorebook_sources",
        "scene_states", "character_agency_states", "story_threads", "memory_embeddings", "pacing_state_cache", "director_plan_versions", "chat_sessions"
    ];

    public static async Task InitializeAsync(
        Re0AgentDbContext context,
        CancellationToken cancellationToken = default)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;", cancellationToken);
            if (await HasLegacyGameStorageAsync(context, cancellationToken))
            {
                await ClearLegacyGameStorageAsync(context, cancellationToken);
            }

            foreach (var statement in DatabaseSchema.CreateStatements)
            {
                await context.Database.ExecuteSqlRawAsync(statement, cancellationToken);
            }

            await EnsureAgentConfigUpgradeColumnsAsync(context, cancellationToken);
            await EnsureRevealQueueUpgradeColumnsAsync(context, cancellationToken);
            await EnsureCharacterCardUpgradeColumnsAsync(context, cancellationToken);
            await EnsurePendingDirectionUpgradeColumnsAsync(context, cancellationToken);
            await EnsureWorldRuntimeUpgradeColumnsAsync(context, cancellationToken);
            await EnsureDirectorPlanUpgradeColumnsAsync(context, cancellationToken);
            await EnsureMemoryEmbeddingUpgradeColumnsAsync(context, cancellationToken);
            await LorebookConditionImporter.ImportAsync(context, cancellationToken);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> HasLegacyGameStorageAsync(
        Re0AgentDbContext context,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(context, "chat_sessions", cancellationToken))
        {
            return false;
        }

        var columns = await ReadColumnNamesAsync(context, "chat_sessions", cancellationToken);
        return columns.Contains("detailed_rounds_snapshot")
            || columns.Contains("current_round_phase")
            || !columns.Contains("game_mode");
    }

    private static async Task ClearLegacyGameStorageAsync(
        Re0AgentDbContext context,
        CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;", cancellationToken);
        try
        {
            foreach (var table in LegacyGameTables)
            {
                await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS " + table + ";", cancellationToken);
            }
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;", cancellationToken);
        }
    }

    private static async Task<bool> TableExistsAsync(
        Re0AgentDbContext context,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
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

    private static async Task EnsureAgentConfigUpgradeColumnsAsync(
        Re0AgentDbContext context,
        CancellationToken cancellationToken)
    {
        var columns = await ReadColumnNamesAsync(context, "agent_config", cancellationToken);
        var additions = new[]
        {
            ("max_input_tokens", "INTEGER DEFAULT 4096"),
            ("response_format", "TEXT DEFAULT 'JSON'"),
            ("enable_thinking", "INTEGER DEFAULT 0"),
            ("reasoning_effort", "TEXT DEFAULT 'medium'"),
            ("auto_retry", "INTEGER DEFAULT 1")
        };

        foreach (var (name, definition) in additions)
        {
            if (!columns.Contains(name))
            {
                await context.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE agent_config ADD COLUMN " + name + " " + definition + ";",
                    cancellationToken);
            }
        }
    }

    private static async Task EnsureRevealQueueUpgradeColumnsAsync(
        Re0AgentDbContext context,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(context, "reveal_queue", cancellationToken)) return;
        var columns = await ReadColumnNamesAsync(context, "reveal_queue", cancellationToken);
        var additions = new[]
        {
            ("occurred_world_time", "TEXT NOT NULL DEFAULT ''"),
            ("importance", "REAL NOT NULL DEFAULT 0.5"),
            ("story_thread_id", "INTEGER"),
            ("allowed_visibility_scope", "TEXT NOT NULL DEFAULT '[]'"),
            ("causal_distance", "INTEGER NOT NULL DEFAULT 0"),
            ("latest_reveal_world_time", "TEXT NOT NULL DEFAULT ''"),
            ("must_reveal", "INTEGER NOT NULL DEFAULT 0")
        };
        foreach (var (name, definition) in additions)
        {
            if (!columns.Contains(name))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE reveal_queue ADD COLUMN " + name + " " + definition + ";", cancellationToken);
        }
    }

    private static async Task EnsureCharacterCardUpgradeColumnsAsync(Re0AgentDbContext context, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(context, "character_card_sources", cancellationToken)) return;
        var columns = await ReadColumnNamesAsync(context, "character_card_sources", cancellationToken);
        var additions = new[]
        {
            ("system_prompt", "TEXT"), ("post_history_instructions", "TEXT"), ("example_dialogues", "TEXT"),
            ("creator", "TEXT"), ("character_version", "TEXT"), ("tags", "TEXT")
        };
        foreach (var (name, definition) in additions)
        {
            if (!columns.Contains(name)) await context.Database.ExecuteSqlRawAsync("ALTER TABLE character_card_sources ADD COLUMN " + name + " " + definition + ";", cancellationToken);
        }
    }

    private static async Task EnsurePendingDirectionUpgradeColumnsAsync(Re0AgentDbContext context, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(context, "pending_directions", cancellationToken)) return;
        var columns = await ReadColumnNamesAsync(context, "pending_directions", cancellationToken);
        var additions = new[]
        {
            ("precondition_chain", "TEXT NOT NULL DEFAULT '[]'"), ("earliest_world_time", "TEXT NOT NULL DEFAULT ''"),
            ("completion_progress", "REAL NOT NULL DEFAULT 0"), ("block_reason", "TEXT")
        };
        foreach (var (name, definition) in additions)
        {
            if (!columns.Contains(name)) await context.Database.ExecuteSqlRawAsync("ALTER TABLE pending_directions ADD COLUMN " + name + " " + definition + ";", cancellationToken);
        }
    }

    private static async Task EnsureWorldRuntimeUpgradeColumnsAsync(Re0AgentDbContext context, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(context, "world_runtime_state", cancellationToken)) return;
        var columns = await ReadColumnNamesAsync(context, "world_runtime_state", cancellationToken);
        var additions = new[]
        {
            ("input_activity_started_at", "TEXT"), ("input_event_count", "INTEGER NOT NULL DEFAULT 0"),
            ("foreground_admission_limit", "INTEGER NOT NULL DEFAULT 1")
        };
        foreach (var (name, definition) in additions)
        {
            if (!columns.Contains(name)) await context.Database.ExecuteSqlRawAsync("ALTER TABLE world_runtime_state ADD COLUMN " + name + " " + definition + ";", cancellationToken);
        }
    }

    private static async Task EnsureDirectorPlanUpgradeColumnsAsync(Re0AgentDbContext context, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(context, "director_plan_versions", cancellationToken)) return;
        var columns = await ReadColumnNamesAsync(context, "director_plan_versions", cancellationToken);
        var additions = new[] { ("changed_story_thread_id", "INTEGER"), ("change_summary", "TEXT NOT NULL DEFAULT 'no_story_thread_change'") };
        foreach (var (name, definition) in additions)
        {
            if (!columns.Contains(name)) await context.Database.ExecuteSqlRawAsync("ALTER TABLE director_plan_versions ADD COLUMN " + name + " " + definition + ";", cancellationToken);
        }
    }

    private static async Task EnsureMemoryEmbeddingUpgradeColumnsAsync(Re0AgentDbContext context, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(context, "memory_embeddings", cancellationToken)) return;
        var columns = await ReadColumnNamesAsync(context, "memory_embeddings", cancellationToken);
        if (!columns.Contains("world_epoch"))
            await context.Database.ExecuteSqlRawAsync("ALTER TABLE memory_embeddings ADD COLUMN world_epoch INTEGER NOT NULL DEFAULT 1;", cancellationToken);
    }
}

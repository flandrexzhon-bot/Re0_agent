using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;

namespace Re0Agent.Tests;

public sealed class DatabaseSchemaTests
{
    [Fact]
    public async Task InitializeAsyncCreatesExpectedSchema()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            await using var context = CreateContext(databasePath);
            await DatabaseInitializer.InitializeAsync(context);

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();

            var tableNames = await ReadStringsAsync(
                connection,
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;");

            Assert.Equal(DatabaseSchema.ExpectedTableNames.Order(), tableNames.Order());
            Assert.DoesNotContain("check_suggestions", tableNames);

            var globalStateColumns = await ReadStringsAsync(connection, "PRAGMA table_info(global_state);", 1);
            Assert.Contains("current_chapter", globalStateColumns);

            var savePointColumns = await ReadStringsAsync(connection, "PRAGMA table_info(save_points);", 1);
            Assert.Contains("world_map_snapshot", savePointColumns);
            Assert.Contains("map_elements_snapshot", savePointColumns);
            Assert.Contains("factions_snapshot", savePointColumns);
        }
        finally
        {
            DeleteIfExists(databasePath);
        }
    }

    [Fact]
    public async Task InitializeAsyncUpgradesExistingSavePointTableWithSnapshotColumns()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE save_points (
                      save_id INTEGER PRIMARY KEY,
                      chapter INT NOT NULL,
                      trigger_reason TEXT NOT NULL,
                      global_state_snapshot TEXT NOT NULL,
                      protagonist_snapshot TEXT NOT NULL,
                      npc_snapshot TEXT NOT NULL,
                      inventory_snapshot TEXT NOT NULL,
                      equipment_snapshot TEXT NOT NULL,
                      quest_snapshot TEXT NOT NULL,
                      created_at TEXT NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await using var context = CreateContext(databasePath);
            await DatabaseInitializer.InitializeAsync(context);

            await using var upgradedConnection = new SqliteConnection($"Data Source={databasePath}");
            await upgradedConnection.OpenAsync();
            var savePointColumns = await ReadStringsAsync(upgradedConnection, "PRAGMA table_info(save_points);", 1);

            Assert.Contains("world_map_snapshot", savePointColumns);
            Assert.Contains("map_elements_snapshot", savePointColumns);
            Assert.Contains("factions_snapshot", savePointColumns);
        }
        finally
        {
            DeleteIfExists(databasePath);
        }
    }

    [Fact]
    public async Task InitializeAsyncUpgradesExistingChatSessionsTableWithRoundVariantsColumn()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE chat_sessions (
                      session_id INTEGER PRIMARY KEY AUTOINCREMENT,
                      session_name TEXT NOT NULL,
                      is_active INTEGER DEFAULT 0 CHECK(is_active IN (0, 1)),
                      created_at TEXT NOT NULL,
                      global_state_snapshot TEXT NOT NULL DEFAULT '{}',
                      protagonist_snapshot TEXT NOT NULL DEFAULT '{}',
                      world_map_snapshot TEXT NOT NULL DEFAULT '[]',
                      map_elements_snapshot TEXT NOT NULL DEFAULT '[]',
                      factions_snapshot TEXT NOT NULL DEFAULT '[]',
                      npc_snapshot TEXT NOT NULL DEFAULT '[]',
                      inventory_snapshot TEXT NOT NULL DEFAULT '[]',
                      equipment_snapshot TEXT NOT NULL DEFAULT '[]',
                      quest_snapshot TEXT NOT NULL DEFAULT '[]',
                      chronicle_snapshot TEXT NOT NULL DEFAULT '[]',
                      character_memory_snapshot TEXT NOT NULL DEFAULT '[]',
                      death_return_log_snapshot TEXT NOT NULL DEFAULT '[]',
                      save_points_snapshot TEXT NOT NULL DEFAULT '[]',
                      detailed_rounds_snapshot TEXT NOT NULL DEFAULT '[]'
                    );
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await using var context = CreateContext(databasePath);
            await DatabaseInitializer.InitializeAsync(context);

            await using var upgradedConnection = new SqliteConnection($"Data Source={databasePath}");
            await upgradedConnection.OpenAsync();
            var chatSessionColumns = await ReadStringsAsync(upgradedConnection, "PRAGMA table_info(chat_sessions);", 1);

            Assert.Contains("parent_session_id", chatSessionColumns);
            Assert.Contains("round_variants_snapshot", chatSessionColumns);
            Assert.Contains("current_round_phase", chatSessionColumns);
            Assert.Contains("interrupted_step", chatSessionColumns);

            await using var insert = upgradedConnection.CreateCommand();
            insert.CommandText = "INSERT INTO chat_sessions (session_name, created_at) VALUES ('旧会话', '2024-04-01 09:00');";
            await insert.ExecuteNonQueryAsync();

            var defaults = await ReadStringsAsync(upgradedConnection, "SELECT round_variants_snapshot FROM chat_sessions;");
            Assert.Equal(["{}"], defaults);

            var phaseDefaults = await ReadStringsAsync(upgradedConnection, "SELECT current_round_phase FROM chat_sessions;");
            Assert.Equal(["Idle"], phaseDefaults);

            var stepDefaults = await ReadStringsAsync(upgradedConnection, "SELECT CAST(interrupted_step AS TEXT) FROM chat_sessions;");
            Assert.Equal(["0"], stepDefaults);
        }
        finally
        {
            DeleteIfExists(databasePath);
        }
    }

    [Fact]
    public async Task EntityMappingsAllowCorePhaseOneRows()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            await using var context = CreateContext(databasePath);
            await DatabaseInitializer.InitializeAsync(context);

            context.GlobalStates.Add(new GlobalState
            {
                RowId = 1,
                CurrentLocation = "王都",
                CurrentMinorRegion = "王都中心",
                CurrentMajorRegion = "露格尼卡",
                ElapsedTime = "0分钟",
                CurTime = "2024-04-01 09:00",
                CurrentChapter = 1,
                IsLewd = "否"
            });

            context.ProtagonistInfo.Add(new ProtagonistInfo
            {
                RowId = 1,
                Name = "菜月昴",
                Gender = "男",
                Age = 17,
                Appearance = "黑发黑眼的少年",
                IdentityText = "异世界来客",
                SelfStatus = "正常",
                LocationName = "王都",
                BaseAttributes = "体质:45 敏捷:55 意志:70",
                SpecialAttributes = "死亡回归:特殊",
                ResourcesText = "无"
            });

            context.ImportantNpcs.Add(new ImportantNpc
            {
                RowId = 1,
                Name = "爱蜜莉雅",
                Gender = "女",
                Age = 114,
                BriefIntro = "银发半精灵少女",
                Appearance = "银发紫瞳，气质温和",
                IdentityText = "王选候选人",
                BaseAttributes = "体质:50 敏捷:60 魔法:85",
                SpecialAttributes = "精灵术:90 冰魔法:85",
                LocationName = "王都",
                RelationsText = "菜月昴:同伴",
                InteractionOptions = "交谈,同行",
                PastExperience = "在王都与主角同行。"
            });

            context.AgentConfig.Add(new AgentConfig
            {
                ConfigId = 1,
                AgentType = "GM",
                AgentName = "GM",
                ApiEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai/",
                ApiKey = "test-key",
                ModelName = "gemini-test",
                Temperature = 0.7,
                MaxTokens = 4096,
                Enabled = 1
            });

            context.SavePoints.Add(new SavePoint
            {
                SaveId = 1,
                Chapter = 1,
                TriggerReason = "round_end",
                GlobalStateSnapshot = "{}",
                ProtagonistSnapshot = "{}",
                WorldMapSnapshot = "[]",
                MapElementsSnapshot = "[]",
                FactionsSnapshot = "[]",
                NpcSnapshot = "[]",
                InventorySnapshot = "[]",
                EquipmentSnapshot = "[]",
                QuestSnapshot = "[]",
                CreatedAt = "2024-04-01 09:00"
            });

            await context.SaveChangesAsync();

            context.CharacterMemory.AddRange(
                new CharacterMemory
                {
                    RowId = 1,
                    CharacterName = "菜月昴",
                    RoundIndex = "R0001",
                    MemoryText = "主角记住了初次抵达王都后的不安。",
                    EmotionalState = "紧张",
                    CreatedAt = "2024-04-01 09:05"
                },
                new CharacterMemory
                {
                    RowId = 2,
                    CharacterName = "爱蜜莉雅",
                    RoundIndex = "R0001",
                    MemoryText = "她注意到菜月昴对王都环境很陌生。",
                    EmotionalState = "关切",
                    CreatedAt = "2024-04-01 09:05"
                });

            await context.SaveChangesAsync();

            Assert.Equal(2, await context.CharacterMemory.CountAsync());
            Assert.Equal("角色Agent", new Re0Agent.Core.Models.CharacterAgentProfile
            {
                CharacterName = "菜月昴",
                IsPlayerControlled = true,
                CurrentStateReference = "protagonist_info:1"
            }.IsPlayerControlled ? "角色Agent" : "NPC");
        }
        finally
        {
            DeleteIfExists(databasePath);
        }
    }

    [Fact]
    public async Task ChineseCheckConstraintsAreApplied()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            await using var context = CreateContext(databasePath);
            await DatabaseInitializer.InitializeAsync(context);

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO inventory (row_id, item_name, item_type, quantity, quality, description)
                VALUES (1, '徽章', '剧情物品', 1, '破损', '用于验证中文 CHECK 约束');
                """;

            await Assert.ThrowsAsync<SqliteException>(async () => await command.ExecuteNonQueryAsync());
        }
        finally
        {
            DeleteIfExists(databasePath);
        }
    }

    private static Re0AgentDbContext CreateContext(string databasePath)
    {
        var options = new DbContextOptionsBuilder<Re0AgentDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

        return new Re0AgentDbContext(options);
    }

    private static string CreateTempDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "re0agent-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.db");
    }

    private static void DeleteIfExists(string databasePath)
    {
        SqliteConnection.ClearAllPools();

        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }
    }

    private static async Task<List<string>> ReadStringsAsync(
        SqliteConnection connection,
        string commandText,
        int ordinal = 0)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(ordinal));
        }

        return values;
    }
}

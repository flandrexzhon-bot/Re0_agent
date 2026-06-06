using System.Text;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Services.Agent;
using Re0Agent.Core.Services.Database;
using Re0Agent.Core.Services.Dice;
using Re0Agent.Core.Services.Llm;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Settings;

namespace Re0Agent.Tests;

public sealed class Phase2AgentTests
{
    [Fact]
    public async Task SseParserReadsContentDeltas()
    {
        const string payload = """
            data: {"choices":[{"delta":{"content":"第一段"}}]}

            data: {"choices":[{"delta":{"content":"第二段"}}]}

            data: [DONE]

            """;

        using var reader = new StringReader(payload);
        var deltas = new List<string>();

        await foreach (var delta in SseParser.ReadContentDeltasAsync(reader))
        {
            deltas.Add(delta);
        }

        Assert.Equal(["第一段", "第二段"], deltas);
    }

    [Fact]
    public void SqlSafetyValidatorRejectsUnsafeStatements()
    {
        var validator = new SqlSafetyValidator();

        Assert.True(validator.ValidateStatement("INSERT INTO chronicle (row_id, code_index, time_span, summary, chronicle_text) VALUES (1, 'AM0001', '2024-04-01 09:00 ~ 2024-04-01 09:10', '摘要', '正文')").IsValid);
        Assert.False(validator.ValidateStatement("DELETE FROM chronicle WHERE row_id = 1").IsValid);
        Assert.False(validator.ValidateStatement("DROP TABLE chronicle").IsValid);
        Assert.False(validator.ValidateStatement("INSERT INTO save_points (save_id) VALUES (1)").IsValid);
        Assert.False(validator.ValidateStatement("UPDATE chronicle SET summary = 'a'; UPDATE chronicle SET summary = 'b'").IsValid);
    }

    [Fact]
    public async Task OrchestratorRunsFakeRoundAndWritesChronicleAndMemory()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            await using var context = CreateContext(databasePath);
            var orchestrator = CreateOrchestrator(context);

            var round = await orchestrator.RunRoundAsync("谨慎观察王都周围的动静。", skipPlayerTurn: false);

            Assert.True(round.UsedFakeClient);
            Assert.NotEmpty(round.Events);
            Assert.Single(round.CharacterTurns);
            Assert.Equal("菜月昴", round.CharacterTurns[0].CharacterName);

            Assert.Equal(1, await context.Chronicle.CountAsync());
            Assert.Equal(1, await context.CharacterMemory.CountAsync());
        }
        finally
        {
            DeleteIfExists(databasePath);
        }
    }

    [Fact]
    public async Task OrchestratorHonorsSkippedPlayerTurn()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            await using var context = CreateContext(databasePath);
            var orchestrator = CreateOrchestrator(context);

            var round = await orchestrator.RunRoundAsync(null, skipPlayerTurn: true);

            var turn = Assert.Single(round.CharacterTurns);
            Assert.True(turn.Skipped);
            Assert.Equal("无", turn.DiceResult?.Command);
        }
        finally
        {
            DeleteIfExists(databasePath);
        }
    }

    [Fact]
    public async Task BeginRoundRunsNpcsAndDefersProtagonist()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            await using var context = CreateContext(databasePath);
            var orchestrator = CreateOrchestrator(context);

            var round = await orchestrator.BeginRoundAsync();

            // 默认世界只有主角在场，无在场NPC：第一阶段不应执行任何角色回合，主角被推迟。
            Assert.Empty(round.CharacterTurns);
            Assert.Single(round.PendingProtagonistProfiles);
            Assert.False(round.DeathReturnTriggered);
            // 第一阶段不应写入编年史。
            Assert.Equal(0, await context.Chronicle.CountAsync());
        }
        finally
        {
            DeleteIfExists(databasePath);
        }
    }

    [Fact]
    public async Task OrchestratorExecutesTemporaryNpcsFromGmOpening()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            await using var context = CreateContext(databasePath);
            var orchestrator = CreateOrchestrator(context);

            var round = await orchestrator.BeginRoundAsync();
            
            // Override GmOpening to test custom slot parsing and temporary NPC execution
            round.GmOpening = """
                【场景描述】
                绿阳季的晨光斜落在王都中心的石板街上。
                
                【行动位号】
                - 1号位：环境与路人反应
                - 2号位：菲特
                - 最后行动：菜月昴
                """;

            await orchestrator.RunNpcTurnsAsync(round);

            // 1号位和2号位NPC均应该有执行记录，并且名字正确
            Assert.Equal(2, round.CharacterTurns.Count);
            Assert.Equal("环境与路人反应", round.CharacterTurns[0].CharacterName);
            Assert.Equal("菲特", round.CharacterTurns[1].CharacterName);
            Assert.False(round.CharacterTurns[0].IsPlayerControlled);
            Assert.False(round.CharacterTurns[1].IsPlayerControlled);
        }
        finally
        {
            DeleteIfExists(databasePath);
        }
    }

    [Fact]
    public async Task CompletePlayerTurnFinalizesRoundAndPersists()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            await using var context = CreateContext(databasePath);
            var orchestrator = CreateOrchestrator(context);

            var round = await orchestrator.BeginRoundAsync();
            var completed = await orchestrator.CompletePlayerTurnAsync(round, "谨慎观察王都周围的动静。", skipPlayerTurn: false);

            Assert.Single(completed.CharacterTurns);
            Assert.Equal("菜月昴", completed.CharacterTurns[0].CharacterName);
            Assert.True(completed.CharacterTurns[0].IsPlayerControlled);
            Assert.NotNull(completed.CompletedAt);
            Assert.Equal(1, await context.Chronicle.CountAsync());
            Assert.Equal(1, await context.CharacterMemory.CountAsync());
        }
        finally
        {
            DeleteIfExists(databasePath);
        }
    }

    [Fact]
    public async Task RagServiceListsAllBuiltInEntries()
    {
        var ragService = new BlackTeaRagService(new BlackTeaImporter(), new ChapterVariantRenderer());

        var entries = await ragService.ListAllEntriesAsync();

        // 内置黑茶世界书应可被定位并解析出条目。
        Assert.NotEmpty(entries);
    }

    [Fact]
    public async Task OrchestratorTransitionsChapterViaGmSummaryOrOpening()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            await using var context = CreateContext(databasePath);
            var orchestrator = CreateOrchestrator(context);

            var round = await orchestrator.BeginRoundAsync();
            

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
                Appearance = "黑发眼眸，身着运动服",
                IdentityText = "被召唤至异世界的少年",
                SelfStatus = "正常",
                LocationName = "王都",
                BaseAttributes = "体质:50"
            });
            await context.SaveChangesAsync();

            var completed = await orchestrator.CompletePlayerTurnAsync(round, "ChapterSwitchRequest", skipPlayerTurn: false);

            Assert.Equal(2, completed.Chapter);
            
            var globalState = await context.GlobalStates.FirstAsync();
            Assert.Equal(2, globalState.CurrentChapter);
            Assert.Contains("[EJS / GM 章节切换]", string.Join("\n", completed.Events));
        }
        finally
        {
            DeleteIfExists(databasePath);
        }
    }

    [Fact]
    public async Task BeginRoundWithCustomOpeningUsesProvidedMessage()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            await using var context = CreateContext(databasePath);
            var orchestrator = CreateOrchestrator(context);

            const string customMessage = "【自定义开场】这是来自世界的神秘意志开场。";
            var round = await orchestrator.BeginRoundAsync(customOpening: customMessage);

            Assert.Equal(customMessage, round.GmOpening);
            Assert.Contains(customMessage, round.Events);
        }
        finally
        {
            DeleteIfExists(databasePath);
        }
    }

    private static AgentOrchestrator CreateOrchestrator(Re0AgentDbContext context)
    {
        var configResolver = new AgentConfigResolver(context);
        var promptComposer = new PromptComposer();
        var fakeClient = new FakeLlmClient();
        var ragService = new BlackTeaRagService(new BlackTeaImporter(), new ChapterVariantRenderer());
        var gmAgent = new GmAgent(context, configResolver, promptComposer, fakeClient, ragService);
        var characterAgent = new CharacterAgentService(context, configResolver, promptComposer, fakeClient, ragService);
        var formAgent = new FormAgent(context, configResolver, promptComposer, fakeClient);
        var executor = new FormAgentSqlExecutor(context, new SqlSafetyValidator());
        var diceEngine = new DiceEngine(
            new DiceCommandParser(),
            new CharacterAttributeProvider(context),
            new SequenceDiceRoller([50, 50, 50, 50]));

        var saveSystem = new SaveSystem(context, diceEngine);

        return new AgentOrchestrator(context, gmAgent, characterAgent, formAgent, executor, diceEngine, saveSystem);
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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Agent;
using Re0Agent.Core.Services.Dice;
using Re0Agent.Core.Services.Settings;

namespace Re0Agent.Tests;

public sealed class Phase3RagAndDiceTests
{
    [Fact]
    public async Task BlackTeaImporterParsesWorldBookEntries()
    {
        var importer = new BlackTeaImporter();
        var entries = await importer.ImportAsync(WorldBookPath());

        Assert.InRange(entries.Count, 200, 220);
        Assert.Contains(entries, entry => entry.Comment.Contains("基础", StringComparison.Ordinal) && entry.Constant);
        Assert.Contains(entries, entry => entry.Keys.Contains("爱蜜莉雅"));
    }

    [Fact]
    public async Task RagServiceInjectsConstantsKeywordMatchesAndCapsContent()
    {
        var ragService = new BlackTeaRagService(new BlackTeaImporter(), new ChapterVariantRenderer());

        var context = await ragService.QueryAsync(new RagQuery
        {
            Text = "爱蜜莉雅 罗兹瓦尔宅邸 王都",
            Chapter = 82,
            MaxCharacters = 14_000
        });

        Assert.NotEmpty(context.Content);
        Assert.True(context.Content.Length <= 14_000);
        Assert.True(context.NonConstantCount <= 8);
        Assert.Contains(context.Matches, match => match.Entry.Constant);
        Assert.Contains(context.Matches, match => !match.Entry.Constant && match.MatchedKeys.Count > 0);
    }

    [Fact]
    public async Task ChapterVariantRendererChoosesDifferentBranches()
    {
        var importer = new BlackTeaImporter();
        var entries = await importer.ImportAsync(WorldBookPath());
        var mansion = Assert.Single(entries.Where(entry => entry.Id == 25));
        var renderer = new ChapterVariantRenderer();

        var chapter77 = renderer.Render(mansion.Content, 77);
        var chapter82 = renderer.Render(mansion.Content, 82);

        Assert.NotEqual(chapter77, chapter82);
        Assert.Contains("预备宅邸", chapter77);
        Assert.Contains("主要居所", chapter82);
        Assert.DoesNotContain("<%_", chapter82);
    }

    [Fact]
    public async Task PromptComposerIncludesRagContext()
    {
        var ragContext = await new BlackTeaRagService(new BlackTeaImporter(), new ChapterVariantRenderer())
            .QueryAsync(new RagQuery { Text = "爱蜜莉雅", Chapter = 1 });
        var prompt = new PromptComposer().ComposeGmOpening(
            null,
            [new CharacterAgentProfile { CharacterName = "菜月昴", IsPlayerControlled = true, CurrentStateReference = "protagonist_info:1" }],
            ragContext);

        Assert.Contains("设定上下文", prompt);
        Assert.Contains("设定条目", prompt);
    }

    [Theory]
    [InlineData("无", "无需检定")]
    [InlineData("必成", "成功")]
    [InlineData("必败", "失败")]
    public async Task DiceEngineHandlesFixedCommands(string command, string outcome)
    {
        await using var context = await CreateSeededContextAsync();
        var engine = CreateEngine(context, [50]);

        var result = await engine.ExecuteAsync(command);

        Assert.Equal(outcome, result.Outcome);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData(1, "大成功", true)]
    [InlineData(12, "极难成功", true)]
    [InlineData(30, "困难成功", true)]
    [InlineData(55, "普通成功", true)]
    [InlineData(70, "失败", false)]
    [InlineData(100, "大失败", false)]
    public async Task DiceEngineEvaluatesCoc7SuccessLevels(int roll, string level, bool isSuccess)
    {
        await using var context = await CreateSeededContextAsync();
        var engine = CreateEngine(context, [roll]);

        var result = await engine.ExecuteAsync("检定 菜月昴 感知");

        Assert.Equal(level, result.SuccessLevel);
        Assert.Equal(isSuccess, result.IsSuccess);
    }

    [Fact]
    public async Task DiceEngineReturnsInvalidForUnknownOrNonNumericAttributes()
    {
        await using var context = await CreateSeededContextAsync();
        var engine = CreateEngine(context, [50]);

        var unknown = await engine.ExecuteAsync("检定 不存在 感知");
        var nonNumeric = await engine.ExecuteAsync("检定 菜月昴 死亡回归");

        Assert.Equal("无效", unknown.Outcome);
        Assert.NotNull(unknown.Error);
        Assert.Equal("无效", nonNumeric.Outcome);
        Assert.NotNull(nonNumeric.Error);
    }

    [Theory]
    [InlineData(25, 30, true)]
    [InlineData(30, 25, false)]
    [InlineData(25, 25, false)]
    public async Task OpposedChecksCompareSuccessRankAndLowerRoll(int attackerRoll, int defenderRoll, bool attackerWins)
    {
        await using var context = await CreateSeededContextAsync();
        var engine = CreateEngine(context, [attackerRoll, defenderRoll]);

        var result = await engine.ExecuteAsync("对抗 菜月昴 感知 vs 爱蜜莉雅 魔法");

        Assert.Equal(attackerWins, result.IsSuccess);
    }

    [Fact]
    public async Task ReZeroMagicAppliesLevelModifierAndGateConsumption()
    {
        await using var context = await CreateSeededContextAsync();
        var engine = CreateEngine(context, [60, 70]);

        var result = await engine.ExecuteAsync("魔法 菜月昴 魔法 等级=Ul 门=门耐久");

        Assert.Equal(60, result.TargetAfterModifiers);
        Assert.True(result.IsSuccess);
        Assert.Equal([60, 70], result.Rolls);
        Assert.Equal("损伤等级+1", result.Tags["门消耗"]);
    }

    [Fact]
    public async Task ReZeroSpecialRulesHandleSpiritMiasmaAndBlessing()
    {
        await using var context = await CreateSeededContextAsync();
        var engine = CreateEngine(context, [96]);

        var inactiveSpirit = await engine.ExecuteAsync("精灵术 爱蜜莉雅 精灵术 活跃=否");
        var miasma = await engine.ExecuteAsync("瘴气 60");
        var blessing = await engine.ExecuteAsync("加护 爱蜜莉雅 精灵术 对抗权能=是");

        Assert.False(inactiveSpirit.IsSuccess);
        Assert.Equal("30", miasma.Tags["新增瘴气"]);
        Assert.False(blessing.IsSuccess);
    }

    [Fact]
    public async Task FakeRoundUsesPhaseThreeDiceResultAndStillWritesRecords()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            await using var context = CreateContext(databasePath);
            var orchestrator = CreateOrchestrator(context, [50, 50, 50, 50]);

            var round = await orchestrator.RunRoundAsync("谨慎观察王都周围的动静。", skipPlayerTurn: false);

            Assert.DoesNotContain(round.CharacterTurns, turn => turn.DiceResult?.Detail?.Contains("Phase2占位判定", StringComparison.Ordinal) == true);
            Assert.Equal(1, await context.Chronicle.CountAsync());
            Assert.Equal(1, await context.CharacterMemory.CountAsync());
        }
        finally
        {
            DeleteIfExists(databasePath);
        }
    }

    private static AgentOrchestrator CreateOrchestrator(Re0AgentDbContext context, IEnumerable<int> rolls)
    {
        var configResolver = new AgentConfigResolver(context);
        var promptComposer = new PromptComposer();
        var fakeClient = new Re0Agent.Core.Services.Llm.FakeLlmClient();
        var ragService = new BlackTeaRagService(new BlackTeaImporter(), new ChapterVariantRenderer());
        var gmAgent = new GmAgent(context, configResolver, promptComposer, fakeClient, ragService);
        var characterAgent = new CharacterAgentService(context, configResolver, promptComposer, fakeClient, ragService);
        var formAgent = new FormAgent(context, configResolver, promptComposer, fakeClient);
        var executor = new Re0Agent.Core.Services.Database.FormAgentSqlExecutor(
            context,
            new Re0Agent.Core.Services.Database.SqlSafetyValidator());
        var diceEngine = CreateEngine(context, rolls);
        var saveSystem = new Re0Agent.Core.Services.Database.SaveSystem(context, diceEngine);

        return new AgentOrchestrator(context, gmAgent, characterAgent, formAgent, executor, diceEngine, saveSystem);
    }

    private static DiceEngine CreateEngine(Re0AgentDbContext context, IEnumerable<int> rolls)
    {
        return new DiceEngine(
            new DiceCommandParser(),
            new CharacterAttributeProvider(context),
            new SequenceDiceRoller(rolls));
    }

    private static async Task<Re0AgentDbContext> CreateSeededContextAsync()
    {
        var context = CreateContext(CreateTempDatabasePath());
        await DatabaseInitializer.InitializeAsync(context);

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
            BaseAttributes = "感知:60; 魔法:80; 门耐久:60",
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
            BaseAttributes = "魔法:60; 体质:50",
            SpecialAttributes = "精灵术:90; 冰魔法:85",
            LocationName = "王都",
            PresenceStatus = "在场",
            RelationsText = "菜月昴:同伴",
            InteractionOptions = "交谈,同行",
            PastExperience = "在王都与主角同行。"
        });

        await context.SaveChangesAsync();
        return context;
    }

    private static string WorldBookPath()
    {
        var root = FindRoot();
        return Path.Combine(root.FullName, "data", "settings", "REZero_BlackTea_v2.0.0.json");
    }

    private static DirectoryInfo FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Re0Agent.sln")))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new DirectoryNotFoundException("Could not locate Re0Agent.sln.");
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
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Agent;
using Re0Agent.Core.Services.Dice;
using Re0Agent.Core.Services.Llm;
using Re0Agent.Core.Services.Settings;

namespace Re0Agent.Tests;

public sealed class Phase3RagAndDiceTests
{
    [Fact]
    public void BlackTeaWorldBookContainsBuiltInEntries()
    {
        var entries = BlackTeaWorldBook.Entries;

        Assert.InRange(entries.Count, 200, 220);
        Assert.Contains(entries, entry => entry.Comment.Contains("基础", StringComparison.Ordinal) && entry.Constant);
        Assert.Contains(entries, entry => entry.Keys.Contains("爱蜜莉雅"));
    }

    [Fact]
    public async Task RagServiceInjectsConstantsKeywordMatchesAndCapsContent()
    {
        var ragService = new BlackTeaRagService(new ChapterVariantRenderer());

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
    public async Task RagServiceFiltersChaptersAndLoadsActiveChapterOnly()
    {
        var ragService = new BlackTeaRagService(new ChapterVariantRenderer());

        var context = await ragService.QueryAsync(new RagQuery
        {
            Text = "第82章 第83章 水门都市普利斯提拉",
            Chapter = 82,
            MaxCharacters = 14_000
        });

        // Chapter 82 entry should be loaded (active chapter)
        Assert.Contains(context.Matches, match => match.Entry.Comment.Contains("第82章", StringComparison.Ordinal));
        // Chapter 83 entry should NOT be loaded (different chapter)
        Assert.DoesNotContain(context.Matches, match => match.Entry.Comment.Contains("第83章", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RagServiceCanExcludeChapterEntriesForCharacterScope()
    {
        var ragService = new BlackTeaRagService(new ChapterVariantRenderer());

        var context = await ragService.QueryAsync(new RagQuery
        {
            Text = "第82章 水门都市 普利斯提拉",
            Chapter = 82,
            IncludeChapterEntries = false,
            MaxCharacters = 14_000
        });

        Assert.DoesNotContain(context.Matches, match => match.Entry.Comment.Contains("第82章", StringComparison.Ordinal));
        Assert.DoesNotContain(context.Matches, match => match.Entry.Comment.Contains("章节设定", StringComparison.Ordinal));
        Assert.Contains(context.Matches, match => match.Entry.Comment.Contains("水门都市", StringComparison.Ordinal));
    }

    [Fact]
    public void PromptMacrosExpandRandomToOneOption()
    {
        const string template = "{{random::A,B,C}}";
        var options = new HashSet<string> { "A", "B", "C" };

        for (var i = 0; i < 50; i++)
        {
            Assert.Contains(PromptMacros.Expand(template), options);
        }

        // 双冒号与单冒号都支持，前后文保留。
        Assert.StartsWith("致", PromptMacros.Expand("{{random:致,献}}你"));
        // 嵌套：内层先展开。
        Assert.Contains(PromptMacros.Expand("{{random::{{random::x,y}},z}}"), new[] { "x", "y", "z" });
        // 无宏内容原样返回。
        Assert.Equal("普通文本", PromptMacros.Expand("普通文本"));
    }

    [Fact]
    public void ChapterVariantRendererChoosesDifferentBranches()
    {
        var entries = BlackTeaWorldBook.Entries;
        var mansion = Assert.Single(entries.Where(entry =>
            entry.Comment.Contains("·地点:", StringComparison.Ordinal) &&
            entry.Comment.Contains("罗兹瓦尔宅邸", StringComparison.Ordinal)));
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
        var ragContext = await new BlackTeaRagService(new ChapterVariantRenderer())
            .QueryAsync(new RagQuery { Text = "爱蜜莉雅", Chapter = 1 });
        var prompt = new PromptComposer().ComposeGmOpening(
            null,
            [new CharacterAgentProfile { CharacterName = "菜月昴", IsPlayerControlled = true, CurrentStateReference = "protagonist_info:1" }],
            "- 最后行动：菜月昴",
            ragContext);

        Assert.Contains("设定上下文", prompt);
        Assert.Contains("设定条目", prompt);
    }

    [Fact]
    public void PromptComposerConstrainsCharacterAgentOutput()
    {
        var prompt = new PromptComposer().ComposeCharacterTurn(
            new CharacterAgentProfile
            {
                CharacterName = "菜月昴",
                IsPlayerControlled = true,
                CurrentStateReference = "protagonist_info:1",
                WorldBookEntryKey = "菜月昴"
            },
            new GameRound
            {
                RoundIndex = "R0001",
                Chapter = 1,
                GmOpening = "王都街头的场景已经展开。"
            },
            [],
            "向爱蜜莉雅道谢",
            new RagContext { Content = "基础设定、王都地点设定、菜月昴角色设定。" });

        Assert.Contains("world_settings", prompt);
        Assert.Contains("全角括号（）", prompt);
        Assert.Contains("不得超过150个中文字符", prompt);
        Assert.Contains("“爱蜜莉雅正是个好人啊！”（微笑着点头）", prompt);
    }

    [Fact]
    public async Task CharacterAgentOnlyReceivesBaseLocationAndOwnCharacterWorldBook()
    {
        await using var context = await CreateSeededContextAsync();
        context.GlobalStates.Add(new GlobalState
        {
            RowId = 1,
            CurrentLocation = "王都",
            CurrentMinorRegion = "王都",
            CurrentMajorRegion = "露格尼卡",
            ElapsedTime = "0分钟",
            CurTime = "2026-06-06 09:00",
            CurrentChapter = 82,
            IsLewd = "否"
        });
        await context.SaveChangesAsync();

        var llmClient = new CapturingLlmClient();
        var service = new CharacterAgentService(
            context,
            new AgentConfigResolver(context),
            new PromptComposer(),
            llmClient,
            new StaticRagService(
            [
                Entry("基础设定", "基础可见", constant: true),
                Entry("⚙️基础&世界设定", "基础世界可见", constant: true),
                Entry("⚙️时间&历法设定", "时间历法可见", constant: true),
                Entry("⚙️货币&收入设定", "货币收入可见", constant: true),
                Entry("⚙️饮食&习惯设定", "饮食习惯可见", constant: true),
                Entry("⚙️玛娜&魔法设定", "玛娜魔法可见", constant: true),
                Entry("⚙️权能&加护设定", "权能加护可见", constant: true),
                Entry("🔆状态栏🔆", "状态栏不可见", constant: true),
                Entry("⚙️全局要求", "全局要求不可见", constant: true),
                Entry("🐉露格尼卡·城市: 👑王都", "王都地点可见", keys: ["王都"]),
                Entry("🕊️爱蜜莉雅阵营·人物: 爱蜜莉雅", "爱蜜莉雅角色可见", keys: ["爱蜜莉雅"]),
                Entry("🕊️爱蜜莉雅阵营·人物: 雷姆", "雷姆角色不可见", keys: ["雷姆"]),
                Entry("🐉露格尼卡·机构: 贤人会", "组织不可见", keys: ["王都"]),
                Entry("第82章(第十六卷)——『开头总由来访者开始』", "章节不可见", keys: ["第82章"])
            ]));

        await service.RunTurnAsync(
            new GameRound { RoundIndex = "R0001", Chapter = 82, GmOpening = "王都街头。" },
            new CharacterAgentProfile
            {
                CharacterName = "爱蜜莉雅",
                IsPlayerControlled = false,
                CurrentStateReference = "important_npc:1",
                WorldBookEntryKey = "爱蜜莉雅"
            },
            null);

        var prompt = Assert.Single(llmClient.LastRequest!.Messages.Where(message => message.Role == "user")).Content;
        Assert.Contains("基础可见", prompt);
        Assert.Contains("基础世界可见", prompt);
        Assert.Contains("时间历法可见", prompt);
        Assert.Contains("货币收入可见", prompt);
        Assert.Contains("饮食习惯可见", prompt);
        Assert.Contains("玛娜魔法可见", prompt);
        Assert.Contains("权能加护可见", prompt);
        Assert.Contains("王都地点可见", prompt);
        Assert.Contains("爱蜜莉雅角色可见", prompt);
        Assert.DoesNotContain("状态栏不可见", prompt);
        Assert.DoesNotContain("全局要求不可见", prompt);
        Assert.DoesNotContain("雷姆角色不可见", prompt);
        Assert.DoesNotContain("组织不可见", prompt);
        Assert.DoesNotContain("章节不可见", prompt);
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
            RelationsText = "菜月昴:同伴",
            InteractionOptions = "交谈,同行",
            PastExperience = "在王都与主角同行。"
        });

        await context.SaveChangesAsync();
        return context;
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

    private static WorldBookEntry Entry(
        string comment,
        string content,
        bool constant = false,
        IReadOnlyList<string>? keys = null)
    {
        return new WorldBookEntry
        {
            Comment = comment,
            Content = content,
            Constant = constant,
            Enabled = true,
            InsertionOrder = 0,
            Keys = keys ?? []
        };
    }

    private sealed class CapturingLlmClient : ILlmClient
    {
        public LlmRequest? LastRequest { get; private set; }

        public Task<LlmResponse> SendChatAsync(
            LlmRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new LlmResponse(request.AgentName, "“好的。”（点头）"));
        }

        public async IAsyncEnumerable<LlmStreamChunk> StreamChatAsync(
            LlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            yield return new LlmStreamChunk(request.AgentName, "“好的。”（点头）", true);
            await Task.CompletedTask;
        }
    }

    private sealed class StaticRagService(IReadOnlyList<WorldBookEntry> entries) : IRagService
    {
        public Task<IReadOnlyList<WorldBookEntry>> ListAllEntriesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(entries);
        }

        public Task<RagContext> QueryAsync(RagQuery query, CancellationToken cancellationToken = default)
        {
            var allowedCategories = query.AllowedCategories?.ToHashSet(StringComparer.Ordinal);
            var matches = entries
                .Where(entry => allowedCategories is null || allowedCategories.Contains(WorldBookCategory.GetKey(entry)))
                .Select(entry => new RagMatch
                {
                    Entry = entry,
                    RenderedContent = entry.Content
                })
                .ToList();

            return Task.FromResult(new RagContext
            {
                Matches = matches,
                Content = string.Join('\n', matches.Select(match => match.RenderedContent))
            });
        }
    }
}

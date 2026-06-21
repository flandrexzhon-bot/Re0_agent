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
        var singleColonResult = PromptMacros.Expand("{{random:致,献}}你");
        Assert.True(singleColonResult.StartsWith("致") || singleColonResult.StartsWith("献"), $"应为'致你'或'献你'，实际'{singleColonResult}'");
        // C# 安全格式 {#random::...#}。
        var hashResult = PromptMacros.Expand("{#random::甲,乙,丙#}后文");
        Assert.True(hashResult.StartsWith("甲") || hashResult.StartsWith("乙") || hashResult.StartsWith("丙"), $"应为'甲/乙/丙后文'，实际'{hashResult}'");
        Assert.EndsWith("后文", hashResult);
        // {#random:...#} 单冒号。
        Assert.Contains(PromptMacros.Expand("{#random:左,右#}"), new[] { "左", "右" });
        // 多行内容中包含 {#random::...#}。
        var multiLine = "前文\n{#random::春,夏,秋,冬#}\n后文";
        var multiResult = PromptMacros.Expand(multiLine);
        Assert.Contains(multiResult, new[] { "前文\n春\n后文", "前文\n夏\n后文", "前文\n秋\n后文", "前文\n冬\n后文" });
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

        Assert.Contains("角色可见世界书", prompt);
        Assert.Contains("全角括号（）", prompt);
        Assert.Contains("不得超过150个中文字符", prompt);
        Assert.Contains("“爱蜜莉雅正是个好人啊！”（微笑着点头）", prompt);
    }

    [Fact]
    public void HistoryInjectionMarksHistoryAsHighestPriority()
    {
        var composer = new PromptComposer();

        var block = composer.ComposeHistoryInjection("菜月昴在王都广场遇袭并死亡回归。");

        Assert.Contains("菜月昴在王都广场遇袭并死亡回归。", block);
        Assert.Contains("最高优先", block);
        Assert.Contains("权重最高", block);

        // 空历史时给出占位，不抛异常。
        var empty = composer.ComposeHistoryInjection(null);
        Assert.Contains("无历史记录", empty);
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

        // 主体提示词为首条 user 消息；第二条 user 是历史深度注入消息。
        var prompt = llmClient.LastRequest!.Messages.First(message => message.Role == "user").Content;
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
        var engine = CreateEngine(context, d6Rolls: [3, 4]);

        var result = await engine.ExecuteAsync(command);

        Assert.Equal(outcome, result.Outcome);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData(6, 6, "大成功", true)]   // raw=12 → 大成功
    [InlineData(1, 1, "大失败", false)]  // raw=2  → 大失败
    [InlineData(5, 6, "成功", true)]     // sum=11, 精神8→mod=-1, total=10 >= DC10
    [InlineData(3, 3, "失败", false)]    // sum=6,  mod=-1, total=5 < DC10
    public async Task DiceEngineEvaluates2d6V2SuccessLevels(int d1, int d2, string level, bool isSuccess)
    {
        await using var context = await CreateSeededContextAsync();
        var engine = CreateEngine(context, d6Rolls: [d1, d2]);
        var result = await engine.ExecuteAsync("检定 菜月昴 精神 目标值=10");

        Assert.Equal(level, result.SuccessLevel);
        Assert.Equal(isSuccess, result.IsSuccess);
    }

    [Theory]
    [InlineData("<content>判定：攻击 #4 vs #2 武器伤害=20</content>", DiceCommandKind.Attack)]
    [InlineData("<thought>x</thought><content>判定：检定 #4 力量 目标值=12</content>", DiceCommandKind.Check)]
    [InlineData("判定：无", DiceCommandKind.None)]
    public void ParserStripsContentTagsAroundJudgement(string raw, DiceCommandKind expectedKind)
    {
        var parser = new DiceCommandParser();
        var command = parser.Parse(raw);

        Assert.Equal(expectedKind, command.Kind);
        Assert.DoesNotContain("<", command.RawText);
    }

    [Fact]
    public async Task DiceEngineEvaluatesFullSuccess()
    {
        // 完全成功：total >= DC+5。力量12→mod=1, DC=5 → need total>=10 → sum>=9 → use [5,5]=10
        await using var context = await CreateSeededContextAsync();
        var engine = CreateEngine(context, d6Rolls: [5, 5]);
        var result = await engine.ExecuteAsync("检定 菜月昴 力量 目标值=5");

        Assert.Equal("完全成功", result.SuccessLevel);
        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData(6, 6, 2, 2, true)]   // attacker sum=12 → 大成功 → always wins
    [InlineData(1, 2, 5, 6, false)]  // attacker sum=3 < defender sum=11 → lose
    [InlineData(1, 1, 5, 6, false)]  // raw=2 → 大失败 → 必输
    public async Task OpposedChecksCompare2d6Totals(int a1, int a2, int d1, int d2, bool attackerWins)
    {
        await using var context = await CreateSeededContextAsync();
        var engine = CreateEngine(context, d6Rolls: [a1, a2, d1, d2]);

        var result = await engine.ExecuteAsync("对抗 菜月昴 力量 vs 爱蜜莉雅 敏捷");

        Assert.Equal(attackerWins, result.IsSuccess);
    }

    [Fact]
    public async Task DiceEngineReturnsInvalidForUnknownCharacter()
    {
        await using var context = await CreateSeededContextAsync();
        var engine = CreateEngine(context, d6Rolls: [3, 4]);

        var result = await engine.ExecuteAsync("检定 不存在 力量 目标值=10");

        Assert.Equal("无效", result.Outcome);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task MiasmaStillUsesD100()
    {
        await using var context = await CreateSeededContextAsync();
        // miasma uses d100 (first queue); engine built with d100 roll
        var engine = new DiceEngine(
            new DiceCommandParser(),
            new CharacterAttributeProvider(context),
            new SequenceDiceRoller(d100Rolls: [96], d6Rolls: []));

        var miasma = await engine.ExecuteAsync("瘴气 60");

        Assert.Equal("30", miasma.Tags["新增瘴气"]);
    }

    [Fact]
    public async Task AuthorityDeathReturnAutoTriggers()
    {
        await using var context = await CreateSeededContextAsync();
        var engine = CreateEngine(context, d6Rolls: [3, 4]);

        var result = await engine.ExecuteAsync("权能 菜月昴 类型=死亡回归");

        Assert.True(result.IsSuccess);
        Assert.Equal("自动触发", result.Outcome);
    }

    [Fact]
    public async Task AttackCommandCalculatesDamageAndSetsIds()
    {
        await using var context = await CreateSeededContextAsync();
        var engine = CreateEngine(context, d6Rolls: [6, 6, 1, 1]); // attacker 大成功

        // 攻击 菜月昴(CharId=4) vs 爱蜜莉雅(CharId=2) 武器伤害=20
        // 力量修正=(12-10)/2=1  护甲=0  最终伤害=20+1-0=21
        var result = await engine.ExecuteAsync("攻击 菜月昴 vs 爱蜜莉雅 武器伤害=20");

        Assert.True(result.IsCombat);
        Assert.True(result.IsSuccess);
        Assert.Equal(21, result.Damage);
    }

    [Fact]
    public async Task CombatResolverDeductsHpAndResourcesByCharId()
    {
        await using var context = await CreateSeededContextAsync();
        var resolver = new CombatResolver(context);

        // 菜月昴(CharId=4, mp5/stam104) 攻击 爱蜜莉雅(CharId=2, hp400)，命中30伤害，技能耗魔3体10。
        var attackResult = new DiceResult
        {
            Command = "攻击", Outcome = "命中", IsCombat = true, IsSuccess = true,
            AttackerId = 4, DefenderId = 2, Damage = 30, ManaCost = 3, StaminaCost = 10
        };

        var combat = await resolver.ApplyAsync(attackResult);

        Assert.False(combat.ProtagonistDied);
        Assert.Equal(370, combat.RemainingHp); // 爱蜜莉雅 400-30
        var attacker = await context.ProtagonistInfo.AsNoTracking().FirstAsync(p => p.CharId == 4);
        Assert.Equal(2, attacker.Mp);        // 5-3
        Assert.Equal(94, attacker.Stamina);  // 104-10
    }

    private static DiceEngine CreateEngine(Re0AgentDbContext context, IEnumerable<int> d6Rolls)
    {
        return new DiceEngine(
            new DiceCommandParser(),
            new CharacterAttributeProvider(context),
            SequenceDiceRoller.ForD6([..d6Rolls]));
    }

    private static async Task<Re0AgentDbContext> CreateSeededContextAsync()
    {
        var context = CreateContext(CreateTempDatabasePath());
        await DatabaseInitializer.InitializeAsync(context);

        context.ProtagonistInfo.Add(new ProtagonistInfo
        {
            RowId = 1,
            CharId = 4,
            Name = "菜月昴",
            Gender = "男",
            Age = 17,
            Appearance = "黑发黑眼的少年",
            IdentityText = "异世界来客",
            SelfStatus = "正常",
            LocationName = "王都",
            BaseAttributes = "力量:12; 敏捷:14; 耐力:10; 智力:11; 精神:8; 魅力:9",
            SpecialAttributes = "死亡回归:特殊",
            ResourcesText = "无",
            Hp = 100, MaxHp = 100, Mp = 5, MaxMp = 5, Stamina = 104, MaxStamina = 104
        });

        context.ImportantNpcs.Add(new ImportantNpc
        {
            RowId = 1,
            CharId = 2,
            Name = "爱蜜莉雅",
            Gender = "女",
            Age = 114,
            BriefIntro = "银发半精灵少女",
            Appearance = "银发紫瞳，气质温和",
            IdentityText = "王选候选人",
            BaseAttributes = "力量:18; 敏捷:45; 耐力:40; 智力:88; 精神:95; 魅力:80",
            SpecialAttributes = "冰魔法:85",
            LocationName = "王都",
            RelationsText = "菜月昴:同伴",
            InteractionOptions = "交谈,同行",
            PastExperience = "在王都与主角同行。",
            Hp = 400, MaxHp = 400, Mp = 915, MaxMp = 915, Stamina = 356, MaxStamina = 356
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

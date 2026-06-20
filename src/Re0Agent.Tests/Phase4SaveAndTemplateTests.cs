using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Services.Database;
using Re0Agent.Core.Services.Dice;

namespace Re0Agent.Tests;

public sealed class Phase4SaveAndTemplateTests
{
    [Fact]
    public async Task SaveSystemCreatesSnapshotsAndProtectsLatestSavePoint()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            await using var context = CreateContext(databasePath);
            await SeedGameStateAsync(context);
            var saveSystem = CreateSaveSystem(context, [50]);

            var first = await saveSystem.CreateSavePointAsync("round_end");
            context.GlobalStates.Single().CurTime = "2024-04-01 09:10";
            await context.SaveChangesAsync();
            var second = await saveSystem.CreateSavePointAsync("round_end");

            var stored = await context.SavePoints.AsNoTracking().SingleAsync(item => item.SaveId == first.SaveId);
            Assert.Contains("王都", stored.WorldMapSnapshot);
            Assert.Contains("徽章", stored.InventorySnapshot);

            Assert.True(await saveSystem.DeleteSavePointAsync(first.SaveId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => saveSystem.DeleteSavePointAsync(second.SaveId));
        }
        finally
        {
            DeleteIfExists(databasePath);
        }
    }

    [Fact]
    public async Task FormExecutorStripsNonIntegerChapterWriteFromGlobalState()
    {
        var databasePath = CreateTempDatabasePath();
        try
        {
            await using var context = CreateContext(databasePath);
            await DatabaseInitializer.InitializeAsync(context);
            context.GlobalStates.Add(new GlobalState
            {
                RowId = 1, CurrentLocation = "王都", CurrentMinorRegion = "王都中心",
                CurrentMajorRegion = "露格尼卡", ElapsedTime = "0分钟",
                CurTime = "2024-04-01 09:00", CurrentChapter = 1, IsLewd = "否"
            });
            await context.SaveChangesAsync();

            var executor = new FormAgentSqlExecutor(context, new SqlSafetyValidator());

            // 填表 Agent 误把 current_chapter 写成文字（INTEGER 列会解析失败）。
            // 既有合法字段(current_location)需保留，违规的 current_chapter 赋值被剔除。
            var sql = "UPDATE global_state SET current_location = '罗兹瓦尔宅邸客房', current_chapter = '第七章：宅邸清晨', is_lewd = '否' WHERE row_id = 1;";

            var result = await executor.ExecuteAsync([sql]);

            context.ChangeTracker.Clear();
            var state = await context.GlobalStates.SingleAsync();
            Assert.Equal("罗兹瓦尔宅邸客房", state.CurrentLocation); // 合法更新保留
            Assert.Equal(1, state.CurrentChapter);                  // 章节未被文字污染
            Assert.Equal(1, result.StatementsExecuted);
        }
        finally
        {
            DeleteIfExists(databasePath);
        }
    }

    [Fact]
    public async Task DeathReturnRestoresGameStateAndKeepsOnlyProtagonistMemory()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            await using var context = CreateContext(databasePath);
            await SeedGameStateAsync(context);
            var saveSystem = CreateSaveSystem(context, [50]);
            var savePoint = await saveSystem.CreateSavePointAsync("round_end");

            context.GlobalStates.Single().CurrentLocation = "小巷";
            context.Inventory.Add(new InventoryItem
            {
                RowId = 2,
                ItemName = "匕首",
                ItemType = "武器",
                Quantity = 1,
                Quality = "普通",
                Description = "死亡线里取得的临时武器"
            });
            context.CharacterMemory.AddRange(
                new CharacterMemory
                {
                    RowId = 1,
                    CharacterName = "菜月昴",
                    RoundIndex = "R0002",
                    MemoryText = "他记得自己在小巷遭遇死亡。",
                    EmotionalState = "惊恐",
                    CreatedAt = "2024-04-01 09:20"
                },
                new CharacterMemory
                {
                    RowId = 2,
                    CharacterName = "爱蜜莉雅",
                    RoundIndex = "R0002",
                    MemoryText = "她看到昴离开了王都广场。",
                    EmotionalState = "担心",
                    CreatedAt = "2024-04-01 09:20"
                });
            await context.SaveChangesAsync();

            var result = await saveSystem.TriggerDeathReturnAsync("测试死因", "AM0001");
            context.ChangeTracker.Clear();

            Assert.Equal(savePoint.SaveId, result.SavePointId);
            Assert.Equal(5, result.MiasmaLevel);
            Assert.Equal("王都", (await context.GlobalStates.SingleAsync()).CurrentLocation);
            Assert.DoesNotContain(await context.Inventory.AsNoTracking().ToListAsync(), item => item.ItemName == "匕首");
            Assert.Equal(1, await context.DeathReturnLog.CountAsync());
            Assert.Equal(1, await context.SavePoints.CountAsync());
            Assert.Equal(1, await context.CharacterMemory.CountAsync());
            Assert.Equal("菜月昴", (await context.CharacterMemory.SingleAsync()).CharacterName);
        }
        finally
        {
            DeleteIfExists(databasePath);
        }
    }

    [Fact]
    public async Task ProtagonistTemplateServiceSeedsAndAppliesDefaultSubaruTemplate()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            await using var context = CreateContext(databasePath);
            var templateService = CreateTemplateService(context, [50]);

            await templateService.EnsureDefaultTemplateAsync();
            var template = await context.ProtagonistTemplates.SingleAsync(item => item.TemplateName == "菜月昴");
            context.ImportantNpcs.Add(CreateNpc(1, "菜月昴"));
            await context.SaveChangesAsync();

            var result = await templateService.ApplyTemplateAsync(template.TemplateId);
            context.ChangeTracker.Clear();

            Assert.Equal("菜月昴", result.ProtagonistName);
            Assert.False(result.AddedSubaruNpc);
            Assert.Equal("菜月昴", (await context.ProtagonistInfo.SingleAsync()).Name);
            Assert.DoesNotContain(await context.ImportantNpcs.AsNoTracking().ToListAsync(), npc => npc.Name == "菜月昴");
            Assert.Equal(1, await context.SavePoints.CountAsync());
        }
        finally
        {
            DeleteIfExists(databasePath);
        }
    }

    [Fact]
    public async Task TemplateLeavesLocationEmptyForFormAgent()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            await using var context = CreateContext(databasePath);
            var templateService = CreateTemplateService(context, [50]);

            await templateService.EnsureDefaultTemplateAsync();
            var template = await context.ProtagonistTemplates.SingleAsync(item => item.TemplateName == "菜月昴");

            // 不再硬编码任何初始地点：地点字段留空，由填表 Agent 生成；章节仍按入参设置。
            var result = await templateService.ApplyTemplateAsync(template.TemplateId, 53);
            context.ChangeTracker.Clear();

            var protagonist = await context.ProtagonistInfo.SingleAsync();
            Assert.Equal(string.Empty, protagonist.LocationName);

            var globalState = await context.GlobalStates.SingleAsync();
            Assert.Equal(string.Empty, globalState.CurrentLocation);
            Assert.Equal(53, globalState.CurrentChapter);

            // 初始地点表应为空（无硬编码地点）。
            Assert.Empty(await context.WorldMapPoints.ToListAsync());
        }
        finally
        {
            DeleteIfExists(databasePath);
        }
    }

    [Fact]
    public async Task ProtagonistTemplateServiceAppliesCustomTemplateWithSubaruNpc()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            await using var context = CreateContext(databasePath);
            await DatabaseInitializer.InitializeAsync(context);
            var templateService = CreateTemplateService(context, [50]);
            var custom = new ProtagonistTemplate
            {
                TemplateName = "原创主角",
                IncludesSubaru = 1,
                BaseData = JsonSerializer.Serialize(new ProtagonistInfo
                {
                    RowId = 1,
                    Name = "阿斯特",
                    Gender = "男",
                    Age = 18,
                    Appearance = "黑发旅人",
                    IdentityText = "异世界来客",
                    SelfStatus = "正常",
                    LocationName = "王都",
                    BaseAttributes = "体质:50; 敏捷:50; 感知:50",
                    SpecialAttributes = "无",
                    ResourcesText = "无"
                }),
                IsDefault = 0
            };
            context.ProtagonistTemplates.Add(custom);
            await context.SaveChangesAsync();

            var result = await templateService.ApplyTemplateAsync(custom.TemplateId);
            context.ChangeTracker.Clear();

            Assert.Equal("阿斯特", result.ProtagonistName);
            Assert.True(result.AddedSubaruNpc);
            Assert.Equal("阿斯特", (await context.ProtagonistInfo.SingleAsync()).Name);
            Assert.Contains(await context.ImportantNpcs.AsNoTracking().ToListAsync(), npc => npc.Name == "菜月昴");
        }
        finally
        {
            DeleteIfExists(databasePath);
        }
    }

    private static SaveSystem CreateSaveSystem(Re0AgentDbContext context, IEnumerable<int> rolls)
    {
        return new SaveSystem(context, CreateEngine(context, rolls));
    }

    private static ProtagonistTemplateService CreateTemplateService(Re0AgentDbContext context, IEnumerable<int> rolls)
    {
        return new ProtagonistTemplateService(context, CreateSaveSystem(context, rolls));
    }

    private static DiceEngine CreateEngine(Re0AgentDbContext context, IEnumerable<int> rolls)
    {
        return new DiceEngine(new DiceCommandParser(), new CharacterAttributeProvider(context), new SequenceDiceRoller(rolls));
    }

    private static async Task SeedGameStateAsync(Re0AgentDbContext context)
    {
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
        context.WorldMapPoints.Add(new WorldMapPoint
        {
            RowId = 1,
            LocationName = "王都",
            MinorRegion = "王都中心",
            MajorRegion = "露格尼卡",
            LocationType = "特殊",
            EnvironmentDesc = "露格尼卡王国的中心",
            Importance = "核心",
            ExplorationStatus = "部分探索"
        });
        context.MapElements.Add(new MapElement
        {
            RowId = 1,
            ElementName = "徽章线索",
            ElementType = "剧情物品",
            LocationName = "王都",
            ElementDesc = "银色徽章线索",
            StatusText = "待调查",
            InteractionOptions = "检查,拾取"
        });
        context.Factions.Add(new Faction
        {
            RowId = 1,
            FactionName = "爱蜜莉雅阵营",
            Description = "支持爱蜜莉雅参与王选",
            Leader = "爱蜜莉雅",
            RelationsText = "菜月昴:合作",
            Headquarters = "罗兹瓦尔宅邸"
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
            BaseAttributes = "感知:60; 意志:70",
            SpecialAttributes = "死亡回归:特殊",
            ResourcesText = "手机"
        });
        context.ImportantNpcs.Add(CreateNpc(1, "爱蜜莉雅"));
        context.Inventory.Add(new InventoryItem
        {
            RowId = 1,
            ItemName = "徽章",
            ItemType = "剧情物品",
            Quantity = 1,
            Quality = "稀有",
            Description = "王选候选人的徽章"
        });
        context.Equipment.Add(new EquipmentItem
        {
            RowId = 1,
            EquipmentName = "运动服",
            EquipmentType = "衣物",
            Quality = "普通",
            StatusText = "已装备",
            Description = "异世界来客的衣物"
        });
        context.Quests.Add(new Quest
        {
            RowId = 1,
            QuestName = "寻找徽章",
            QuestType = "主线",
            PriorityLevel = "重要",
            TargetDesc = "找回被盗的王选徽章",
            ProgressText = "0%",
            StatusTag = "进行中",
            SourceText = "爱蜜莉雅",
            RewardText = "信任"
        });
        context.Chronicle.Add(new ChronicleEntry
        {
            RowId = 1,
            CodeIndex = "AM0001",
            TimeSpan = "2024-04-01 09:00 ~ 2024-04-01 09:10",
            Summary = "王都初始场景",
            ChronicleText = string.Concat(Enumerable.Repeat("菜月昴在王都醒来，确认自己身处陌生环境，并开始寻找徽章线索。", 8))
        });
        await context.SaveChangesAsync();
    }

    private static ImportantNpc CreateNpc(int rowId, string name)
    {
        return new ImportantNpc
        {
            RowId = rowId,
            Name = name,
            Gender = name == "菜月昴" ? "男" : "女",
            Age = name == "菜月昴" ? 17 : 114,
            BriefIntro = name == "菜月昴" ? "黑发黑眼少年" : "银发半精灵少女",
            Appearance = name == "菜月昴" ? "黑发黑眼，穿运动服" : "银发紫瞳，气质温和",
            IdentityText = name == "菜月昴" ? "异世界来客" : "王选候选人",
            BaseAttributes = "感知:60; 魔法:80",
            SpecialAttributes = name == "菜月昴" ? "死亡回归:特殊" : "精灵术:90",
            LocationName = "王都",
            RelationsText = "菜月昴:同伴",
            InteractionOptions = "交谈,同行",
            PastExperience = "在王都与主角同行。"
        };
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

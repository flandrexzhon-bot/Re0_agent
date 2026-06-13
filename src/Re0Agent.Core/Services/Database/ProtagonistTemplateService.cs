using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;

namespace Re0Agent.Core.Services.Database;

public sealed record TemplateApplyResult(
    int TemplateId,
    string TemplateName,
    string ProtagonistName,
    bool AddedSubaruNpc,
    int SavePointId);

public sealed class ProtagonistTemplateService(
    Re0AgentDbContext dbContext,
    SaveSystem saveSystem)
{
    private const string SubaruName = "菜月昴";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task EnsureDefaultTemplateAsync(CancellationToken cancellationToken = default)
    {
        await DatabaseInitializer.InitializeAsync(dbContext, cancellationToken);

        var exists = await dbContext.ProtagonistTemplates
            .AnyAsync(template => template.TemplateName == SubaruName, cancellationToken);
        if (exists)
        {
            return;
        }

        dbContext.ProtagonistTemplates.Add(new ProtagonistTemplate
        {
            TemplateName = SubaruName,
            IncludesSubaru = 0,
            BaseData = JsonSerializer.Serialize(CreateDefaultSubaruProtagonist(), JsonOptions),
            IsDefault = 1
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProtagonistTemplate>> ListTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureDefaultTemplateAsync(cancellationToken);

        return await dbContext.ProtagonistTemplates.AsNoTracking()
            .OrderByDescending(template => template.IsDefault)
            .ThenBy(template => template.TemplateId)
            .ToListAsync(cancellationToken);
    }

    public async Task<TemplateApplyResult> ApplyTemplateAsync(
        int templateId,
        CancellationToken cancellationToken)
    {
        return await ApplyTemplateAsync(templateId, 1, cancellationToken);
    }

    public async Task<TemplateApplyResult> ApplyTemplateAsync(
        int templateId,
        int startingChapter = 1,
        CancellationToken cancellationToken = default)
    {
        await EnsureDefaultTemplateAsync(cancellationToken);

        var template = await dbContext.ProtagonistTemplates.AsNoTracking()
            .FirstOrDefaultAsync(item => item.TemplateId == templateId, cancellationToken)
            ?? throw new InvalidOperationException("找不到指定主角模板。");

        var protagonist = ReadTemplateProtagonist(template.BaseData);
        protagonist.RowId = 1;

        var startingLocation = startingChapter switch
        {
            1 => "王都",
            7 => "罗兹瓦尔宅邸",
            18 => "王都",
            53 => "圣域",
            82 => "水门都市",
            _ => "王都"
        };
        protagonist.LocationName = startingLocation;

        var addedSubaruNpc = false;
        var transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            dbContext.ChangeTracker.Clear();

            await EnsureMinimumWorldStateAsync(protagonist, startingChapter, cancellationToken);
            await SeedImportantNpcsForChapterAsync(startingChapter, protagonist, cancellationToken);
            
            await dbContext.ProtagonistInfo.ExecuteDeleteAsync(cancellationToken);
            dbContext.ProtagonistInfo.Add(protagonist);

            if (template.IncludesSubaru == 1 && !string.Equals(protagonist.Name, SubaruName, StringComparison.Ordinal))
            {
                await UpsertSubaruNpcAsync(protagonist, cancellationToken);
                addedSubaruNpc = true;
            }
            else
            {
                await dbContext.ImportantNpcs
                    .Where(npc => npc.Name == SubaruName)
                    .ExecuteDeleteAsync(cancellationToken);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            throw;
        }

        var savePoint = await saveSystem.CreateSavePointAsync("initial_template", cancellationToken);
        return new TemplateApplyResult(
            template.TemplateId,
            template.TemplateName,
            protagonist.Name,
            addedSubaruNpc,
            savePoint.SaveId);
    }

    public async Task CreateTemplateFromProtagonistAsync(
        string templateName,
        ProtagonistInfo protagonist,
        CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.ProtagonistTemplates
            .AnyAsync(t => t.TemplateName == templateName, cancellationToken);
        if (existing)
        {
            throw new InvalidOperationException($"模板名称 '{templateName}' 已存在。");
        }

        var cleanProtagonist = new ProtagonistInfo
        {
            RowId = 1,
            Name = protagonist.Name,
            Gender = protagonist.Gender,
            Age = protagonist.Age,
            Appearance = protagonist.Appearance,
            IdentityText = protagonist.IdentityText,
            SelfStatus = protagonist.SelfStatus ?? "正常",
            LocationName = protagonist.LocationName ?? "王都",
            BaseAttributes = protagonist.BaseAttributes,
            SpecialAttributes = protagonist.SpecialAttributes,
            ResourcesText = protagonist.ResourcesText
        };

        var baseData = JsonSerializer.Serialize(new { protagonist = cleanProtagonist }, JsonOptions);

        dbContext.ProtagonistTemplates.Add(new ProtagonistTemplate
        {
            TemplateName = templateName,
            IncludesSubaru = string.Equals(cleanProtagonist.Name, SubaruName, StringComparison.Ordinal) ? 0 : 1,
            BaseData = baseData,
            IsDefault = 0
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteTemplateAsync(int templateId, CancellationToken cancellationToken = default)
    {
        var template = await dbContext.ProtagonistTemplates
            .FirstOrDefaultAsync(t => t.TemplateId == templateId, cancellationToken);
        if (template is null) return;
        if (template.IsDefault == 1)
        {
            throw new InvalidOperationException("无法删除默认模板。");
        }

        dbContext.ProtagonistTemplates.Remove(template);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureMinimumWorldStateAsync(
        ProtagonistInfo protagonist,
        int startingChapter,
        CancellationToken cancellationToken)
    {
        // Clear old state tables so a new game start resets everything correctly
        await dbContext.GlobalStates.ExecuteDeleteAsync(cancellationToken);
        await dbContext.WorldMapPoints.ExecuteDeleteAsync(cancellationToken);
        await dbContext.MapElements.ExecuteDeleteAsync(cancellationToken);
        await dbContext.Factions.ExecuteDeleteAsync(cancellationToken);
        await dbContext.Inventory.ExecuteDeleteAsync(cancellationToken);
        await dbContext.Equipment.ExecuteDeleteAsync(cancellationToken);
        await dbContext.Quests.ExecuteDeleteAsync(cancellationToken);
        await dbContext.Chronicle.ExecuteDeleteAsync(cancellationToken);
        await dbContext.CharacterMemory.ExecuteDeleteAsync(cancellationToken);
        await dbContext.DeathReturnLog.ExecuteDeleteAsync(cancellationToken);
        await dbContext.SavePoints.ExecuteDeleteAsync(cancellationToken);
        await dbContext.ImportantNpcs.ExecuteDeleteAsync(cancellationToken);

        var (majorRegion, minorRegion) = GetRegionForLocation(protagonist.LocationName);

        dbContext.GlobalStates.Add(new GlobalState
        {
            RowId = 1,
            CurrentLocation = protagonist.LocationName,
            CurrentMinorRegion = minorRegion,
            CurrentMajorRegion = majorRegion,
            ElapsedTime = "0分钟",
            CurTime = "2024-04-01 09:00",
            CurrentChapter = startingChapter,
            IsLewd = "否"
        });

        dbContext.WorldMapPoints.Add(new WorldMapPoint
        {
            RowId = 1,
            LocationName = protagonist.LocationName,
            MinorRegion = minorRegion,
            MajorRegion = majorRegion,
            LocationType = "特殊",
            EnvironmentDesc = "主角模板初始化地点",
            Importance = "核心",
            ExplorationStatus = "部分探索"
        });
    }

    private static (string Major, string Minor) GetRegionForLocation(string location)
    {
        return location switch
        {
            "罗兹瓦尔宅邸" => ("梅札斯领", "宅邸"),
            "圣域" => ("克莱恩乡", "圣域墓地"),
            "水门都市" => ("利契亚", "水门都市"),
            _ => ("露格尼卡", "王都中心")
        };
    }

    private async Task UpsertSubaruNpcAsync(
        ProtagonistInfo protagonist,
        CancellationToken cancellationToken)
    {
        await dbContext.ImportantNpcs
            .Where(npc => npc.Name == SubaruName)
            .ExecuteDeleteAsync(cancellationToken);

        dbContext.ImportantNpcs.Add(new ImportantNpc
        {
            RowId = await NextRowIdAsync(dbContext.ImportantNpcs, cancellationToken),
            Name = SubaruName,
            Gender = "男",
            Age = 17,
            BriefIntro = "黑发黑眼的异世界少年",
            Appearance = "黑发黑眼，穿运动服的少年",
            IdentityText = "异世界来客",
            BaseAttributes = "体质:45; 敏捷:55; 感知:60; 意志:70",
            SpecialAttributes = "死亡回归:特殊",
            LocationName = protagonist.LocationName,
            RelationsText = $"{protagonist.Name}:同行",
            InteractionOptions = "交谈,同行",
            PastExperience = "作为异世界来客卷入当前事件。"
        });
    }

    private static ProtagonistInfo ReadTemplateProtagonist(string baseData)
    {
        using var document = JsonDocument.Parse(baseData);
        var element = document.RootElement.TryGetProperty("protagonist", out var protagonistElement)
            ? protagonistElement
            : document.RootElement;

        return element.Deserialize<ProtagonistInfo>(JsonOptions)
            ?? throw new InvalidOperationException("主角模板数据无法解析。");
    }

    private static ProtagonistInfo CreateDefaultSubaruProtagonist()
    {
        return new ProtagonistInfo
        {
            RowId = 1,
            Name = SubaruName,
            Gender = "男",
            Age = 17,
            Appearance = "黑发黑眼，穿运动服的少年",
            IdentityText = "异世界来客",
            SelfStatus = "正常",
            LocationName = "王都",
            BaseAttributes = "体质:45; 敏捷:55; 感知:60; 意志:70",
            SpecialAttributes = "死亡回归:特殊",
            ResourcesText = "手机; 零基础异世界知识"
        };
    }

    private static async Task<int> NextRowIdAsync<TEntity>(
        DbSet<TEntity> set,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        return await set.Select(entity => EF.Property<int>(entity, "RowId"))
            .DefaultIfEmpty()
            .MaxAsync(cancellationToken) + 1;
    }

    private async Task SeedImportantNpcsForChapterAsync(
        int startingChapter,
        ProtagonistInfo protagonist,
        CancellationToken cancellationToken)
    {
        var npcs = new List<ImportantNpc>();
        int rowId = await NextRowIdAsync(dbContext.ImportantNpcs, cancellationToken);

        if (startingChapter == 1)
        {
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "爱蜜莉雅",
                Gender = "女",
                Age = 115,
                BriefIntro = "银发紫眸的半精灵少女",
                Appearance = "身着白色衣装的银发少女",
                IdentityText = "露格尼卡王国候选王选人之一",
                BaseAttributes = "力量:40; 敏捷:65; 智力:85; 意志:75",
                SpecialAttributes = "微精灵使:极高",
                LocationName = protagonist.LocationName,
                RelationsText = $"{protagonist.Name}:初见",
                InteractionOptions = "交谈,送礼,切磋",
                PastExperience = "在王都寻找丢失的徽章时遭遇袭击。"
            });
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "帕克",
                Gender = "男",
                Age = 420,
                BriefIntro = "爱蜜莉雅的契约大精灵",
                Appearance = "形似猫咪的灰色小兽",
                IdentityText = "四大精灵之一，永久冻土终焉之兽",
                BaseAttributes = "力量:80; 敏捷:90; 智力:95; 意志:90",
                SpecialAttributes = "阴魔法:精通; 终焉化:终极",
                LocationName = protagonist.LocationName,
                RelationsText = $"{protagonist.Name}:防备",
                InteractionOptions = "抚摸,交谈",
                PastExperience = "守护爱蜜莉雅的猫型精灵。"
            });
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "菲鲁特",
                Gender = "女",
                Age = 15,
                BriefIntro = "贫民街长大的金发盗贼少女",
                Appearance = "金发红眸，身手敏捷的小女孩",
                IdentityText = "王选候补者（身世神秘）",
                BaseAttributes = "力量:30; 敏捷:85; 智力:60; 意志:70",
                SpecialAttributes = "风之加护:速度极快",
                LocationName = "王都贫民街",
                RelationsText = $"{protagonist.Name}:陌生",
                InteractionOptions = "交谈,交易",
                PastExperience = "受委托盗取了爱蜜莉雅的徽章。"
            });
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "罗姆爷",
                Gender = "男",
                Age = 87,
                BriefIntro = "贫民街赃物库的巨人族老人",
                Appearance = "身材魁梧、满脸胡须的巨人族",
                IdentityText = "赃物库主人，前亚人联盟参谋",
                BaseAttributes = "力量:70; 敏捷:35; 智力:65; 意志:75",
                SpecialAttributes = "巨人血统:怪力",
                LocationName = "王都贫民街",
                RelationsText = $"{protagonist.Name}:陌生",
                InteractionOptions = "交谈,交易",
                PastExperience = "在赃物库照料菲鲁特的巨人族老人。"
            });
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "莱因哈尔德",
                Gender = "男",
                Age = 19,
                BriefIntro = "红发蓝眸的当代「剑圣」",
                Appearance = "红发帅气的青年骑士",
                IdentityText = "阿斯特雷亚家族长子，露格尼卡守护神",
                BaseAttributes = "力量:99; 敏捷:99; 智力:90; 意志:99",
                SpecialAttributes = "避箭加护:免疫远程; 剑圣加护:无敌",
                LocationName = protagonist.LocationName,
                RelationsText = $"{protagonist.Name}:友好",
                InteractionOptions = "求教,交谈",
                PastExperience = "在王都巡逻时听到呼救而介入战斗。"
            });
        }
        else if (startingChapter == 7)
        {
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "拉姆",
                Gender = "女",
                Age = 17,
                BriefIntro = "罗兹瓦尔宅邸的女仆姐姐",
                Appearance = "粉发红眸的鬼族少女",
                IdentityText = "罗兹瓦尔宅邸女仆长",
                BaseAttributes = "力量:45; 敏捷:60; 智力:80; 意志:75",
                SpecialAttributes = "风魔法:熟练; 千里眼:视界共享",
                LocationName = protagonist.LocationName,
                RelationsText = $"{protagonist.Name}:冷漠",
                InteractionOptions = "学习家务,交谈",
                PastExperience = "曾是鬼族神童，失去角后寄身于宅邸。"
            });
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "雷姆",
                Gender = "女",
                Age = 17,
                BriefIntro = "罗兹瓦尔宅邸的女仆妹妹",
                Appearance = "蓝发蓝眸的鬼族少女",
                IdentityText = "罗兹瓦尔宅邸女仆",
                BaseAttributes = "力量:65; 敏捷:70; 智力:70; 意志:80",
                SpecialAttributes = "水魔法:熟练; 鬼化:战力暴增",
                LocationName = protagonist.LocationName,
                RelationsText = $"{protagonist.Name}:戒备",
                InteractionOptions = "协助工作,交谈",
                PastExperience = "对姐姐心存愧疚，极其勤恳地维持宅邸运转。"
            });
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "碧翠丝",
                Gender = "女",
                Age = 400,
                BriefIntro = "守护禁书库的精致金发幼女",
                Appearance = "身穿华丽洋装的螺旋金发萝莉",
                IdentityText = "禁书库管理员，阴属性大精灵",
                BaseAttributes = "力量:30; 敏捷:50; 智力:95; 意志:90",
                SpecialAttributes = "空间传送:禁书库连接; 阴魔法:极致",
                LocationName = protagonist.LocationName,
                RelationsText = $"{protagonist.Name}:嫌弃",
                InteractionOptions = "阅读,搭讪",
                PastExperience = "在禁书库中等待「那个人」长达四百年。"
            });
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "爱蜜莉雅",
                Gender = "女",
                Age = 115,
                BriefIntro = "银发紫眸的半精灵少女",
                Appearance = "身着白色衣装的银发少女",
                IdentityText = "露格尼卡王国候选王选人之一",
                BaseAttributes = "力量:40; 敏捷:65; 智力:85; 意志:75",
                SpecialAttributes = "微精灵使:极高",
                LocationName = protagonist.LocationName,
                RelationsText = $"{protagonist.Name}:信赖",
                InteractionOptions = "学习,交谈",
                PastExperience = "带主角回到宅邸休养。"
            });
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "帕克",
                Gender = "男",
                Age = 420,
                BriefIntro = "爱蜜莉雅的契约大精灵",
                Appearance = "形似猫咪的灰色小兽",
                IdentityText = "四大精灵之一，永久冻土终焉之兽",
                BaseAttributes = "力量:80; 敏捷:90; 智力:95; 意志:90",
                SpecialAttributes = "阴魔法:精通; 终焉化:终极",
                LocationName = protagonist.LocationName,
                RelationsText = $"{protagonist.Name}:友好",
                InteractionOptions = "抚摸,交谈",
                PastExperience = "守护爱蜜莉雅的猫型精灵。"
            });
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "罗兹瓦尔",
                Gender = "男",
                Age = 35,
                BriefIntro = "画着小丑妆容的露格尼卡贵族",
                Appearance = "身着小丑华服、左右异瞳的男子",
                IdentityText = "梅札斯边境伯，首席宫廷魔导士",
                BaseAttributes = "力量:75; 敏捷:80; 智力:98; 意志:85",
                SpecialAttributes = "六翼加护:六属魔法极致",
                LocationName = protagonist.LocationName,
                RelationsText = $"{protagonist.Name}:审视",
                InteractionOptions = "谈话,请教",
                PastExperience = "宅邸主人，支持爱蜜莉雅进行王选。"
            });
        }
        else if (startingChapter == 18)
        {
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "雷姆",
                Gender = "女",
                Age = 17,
                BriefIntro = "深爱着主角的蓝发鬼族女仆",
                Appearance = "蓝发蓝眸的鬼族少女",
                IdentityText = "罗兹瓦尔宅邸女仆，主角的支柱",
                BaseAttributes = "力量:65; 敏捷:70; 智力:70; 意志:95",
                SpecialAttributes = "水魔法:精通; 鬼化:战力暴增",
                LocationName = protagonist.LocationName,
                RelationsText = $"{protagonist.Name}:狂热",
                InteractionOptions = "倾诉,协同战斗",
                PastExperience = "在无数次轮回中被主角拯救，视主角为自己的英雄。"
            });
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "拉姆",
                Gender = "女",
                Age = 17,
                BriefIntro = "罗兹瓦尔宅邸的粉发鬼族女仆",
                Appearance = "粉发红眸的鬼族少女",
                IdentityText = "罗兹瓦尔宅邸女仆长",
                BaseAttributes = "力量:45; 敏捷:60; 智力:80; 意志:75",
                SpecialAttributes = "风魔法:熟练; 千里眼:视界共享",
                LocationName = protagonist.LocationName,
                RelationsText = $"{protagonist.Name}:毒舌",
                InteractionOptions = "交谈",
                PastExperience = "留在宅邸处理事务，并提防外部威胁。"
            });
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "菲利克斯",
                Gender = "男",
                Age = 19,
                BriefIntro = "拥有猫耳猫尾的露格尼卡骑士",
                Appearance = "身穿骑士服、长有猫耳的青葱少年",
                IdentityText = "库珥修的骑士，大陆第一治愈魔法使",
                BaseAttributes = "力量:35; 敏捷:75; 智力:90; 意志:80",
                SpecialAttributes = "水之加护:顶级治愈魔法",
                LocationName = protagonist.LocationName,
                RelationsText = $"{protagonist.Name}:淡然",
                InteractionOptions = "治疗,交谈",
                PastExperience = "代表库珥修阵营在王都负责联络与医疗事务。"
            });
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "威尔海姆",
                Gender = "男",
                Age = 61,
                BriefIntro = "被称为「剑鬼」的年迈老绅士",
                Appearance = "身穿黑西装、眼神锐利的白发老人",
                IdentityText = "库珥修阵营家臣，当代顶尖剑士",
                BaseAttributes = "力量:85; 敏捷:90; 智力:80; 意志:95",
                SpecialAttributes = "剑鬼技艺:超凡剑术",
                LocationName = protagonist.LocationName,
                RelationsText = $"{protagonist.Name}:欣赏",
                InteractionOptions = "切磋,讨教",
                PastExperience = "为报妻仇苦练剑术一生的复仇者。"
            });
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "爱蜜莉雅",
                Gender = "女",
                Age = 115,
                BriefIntro = "银发紫眸的半精灵少女",
                Appearance = "身着白色衣装的银发少女",
                IdentityText = "露格尼卡王国候选王选人之一",
                BaseAttributes = "力量:40; 敏捷:65; 智力:85; 意志:75",
                SpecialAttributes = "微精灵使:极高",
                LocationName = protagonist.LocationName,
                RelationsText = $"{protagonist.Name}:担忧",
                InteractionOptions = "解释,交谈",
                PastExperience = "来到王都参加王选，极力避免主角卷入危险。"
            });
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "库珥修",
                Gender = "女",
                Age = 20,
                BriefIntro = "公爵家当家，英姿飒爽的女杰",
                Appearance = "绿发绿眸、常穿男装的英气女子",
                IdentityText = "卡尔斯腾公爵，王国第一候选人",
                BaseAttributes = "力量:70; 敏捷:75; 智力:90; 意志:95",
                SpecialAttributes = "风之加护:看穿谎言与斩击风刃",
                LocationName = "库珥修宅邸",
                RelationsText = $"{protagonist.Name}:敬重",
                InteractionOptions = "结盟,交谈",
                PastExperience = "以极强的手腕和公正态度统率公爵领。"
            });
        }
        else if (startingChapter == 53)
        {
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "法兰黛莉卡",
                Gender = "女",
                Age = 21,
                BriefIntro = "返聘归来的宅邸女仆长",
                Appearance = "拥有尖牙、身材高挑的金发女仆",
                IdentityText = "罗兹瓦尔宅邸女仆，亚人混血",
                BaseAttributes = "力量:75; 敏捷:70; 智力:75; 意志:80",
                SpecialAttributes = "兽化:战斗力翻倍",
                LocationName = protagonist.LocationName,
                RelationsText = $"{protagonist.Name}:友好",
                InteractionOptions = "打听消息,交谈",
                PastExperience = "在宅邸人手空缺时受雇重返岗位。"
            });
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "佩特拉",
                Gender = "女",
                Age = 12,
                BriefIntro = "阿拉姆村出身的新晋女仆",
                Appearance = "红发活泼的少女",
                IdentityText = "罗兹瓦尔宅邸见习女仆",
                BaseAttributes = "力量:25; 敏捷:50; 智力:65; 意志:70",
                SpecialAttributes = "无",
                LocationName = protagonist.LocationName,
                RelationsText = $"{protagonist.Name}:仰慕",
                InteractionOptions = "交谈,关怀",
                PastExperience = "在魔兽事件中被拯救，决定成为女仆报答。"
            });
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "雷姆（昏睡）",
                Gender = "女",
                Age = 17,
                BriefIntro = "因暴食权能沉睡在客房的少女",
                Appearance = "蓝发蓝眸，面容安详睡在床上的少女",
                IdentityText = "主角深爱的女仆，被夺去名字与记忆",
                BaseAttributes = "力量:0; 敏捷:0; 智力:0; 意志:0",
                SpecialAttributes = "无",
                LocationName = "宅邸客房",
                RelationsText = $"{protagonist.Name}:羁绊",
                InteractionOptions = "探望",
                PastExperience = "与暴食大罪司教遭遇战中被夺去存在感。"
            });
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "爱蜜莉雅",
                Gender = "女",
                Age = 115,
                BriefIntro = "银发紫眸的半精灵少女",
                Appearance = "身着白色衣装的银发少女",
                IdentityText = "露格尼卡王国候选王选人之一",
                BaseAttributes = "力量:40; 敏捷:65; 智力:85; 意志:75",
                SpecialAttributes = "微精灵使:极高",
                LocationName = protagonist.LocationName,
                RelationsText = $"{protagonist.Name}:依赖",
                InteractionOptions = "交谈,同行",
                PastExperience = "准备出发前往圣域解决结界危机。"
            });
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "碧翠丝",
                Gender = "女",
                Age = 400,
                BriefIntro = "守护禁书库的精致金发幼女",
                Appearance = "身穿华丽洋装的螺旋金发萝莉",
                IdentityText = "禁书库管理员，阴属性大精灵",
                BaseAttributes = "力量:30; 敏捷:50; 智力:95; 意志:90",
                SpecialAttributes = "空间传送:禁书库连接; 阴魔法:极致",
                LocationName = protagonist.LocationName,
                RelationsText = $"{protagonist.Name}:复杂",
                InteractionOptions = "质问,交谈",
                PastExperience = "因福音书内容与内心防线与主角对峙。"
            });
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "加菲尔",
                Gender = "男",
                Age = 14,
                BriefIntro = "「圣域之盾」亚人少年",
                Appearance = "利齿金发、额头上有一道伤疤的少年",
                IdentityText = "圣域守护者，法兰黛莉卡的弟弟",
                BaseAttributes = "力量:85; 敏捷:80; 智力:50; 意志:85",
                SpecialAttributes = "地灵加护:大地之上恢复提升",
                LocationName = "圣域",
                RelationsText = $"{protagonist.Name}:敌视",
                InteractionOptions = "挑衅,切磋",
                PastExperience = "以极强实力和古怪谚语防卫着圣域的结界。"
            });
        }
        else if (startingChapter == 82)
        {
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "爱蜜莉雅",
                Gender = "女",
                Age = 115,
                BriefIntro = "银发紫眸的半精灵少女",
                Appearance = "身着白色衣装的银发少女",
                IdentityText = "露格尼卡王国候选王选人之一",
                BaseAttributes = "力量:40; 敏捷:65; 智力:85; 意志:85",
                SpecialAttributes = "大精灵契约:与碧翠丝共斗; 冰魔法:极高",
                LocationName = protagonist.LocationName,
                RelationsText = $"{protagonist.Name}:相爱",
                InteractionOptions = "执手,交谈",
                PastExperience = "受安娜塔西亚邀请，携手主角等人前往水门都市。"
            });
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "碧翠丝",
                Gender = "女",
                Age = 400,
                BriefIntro = "与主角契约的螺旋金发幼女",
                Appearance = "身穿华丽洋装的螺旋金发萝莉",
                IdentityText = "主角的契约精灵，同生共死的羁绊",
                BaseAttributes = "力量:30; 敏捷:60; 智力:95; 意志:95",
                SpecialAttributes = "阴魔法:精通; 结界防卫:超凡",
                LocationName = protagonist.LocationName,
                RelationsText = $"{protagonist.Name}:相伴",
                InteractionOptions = "牵手,交谈",
                PastExperience = "跨出禁书库，选择主角作为自己唯一的契约者。"
            });
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "安娜塔西亚",
                Gender = "女",
                Age = 22,
                BriefIntro = "合辛商会会长，王选候选人之一",
                Appearance = "身着白色毛皮披肩的淡紫发女子",
                IdentityText = "卡拉拉基大商会领袖",
                BaseAttributes = "力量:20; 敏捷:40; 智力:95; 意志:80",
                SpecialAttributes = "商业嗅觉:无双",
                LocationName = protagonist.LocationName,
                RelationsText = $"{protagonist.Name}:商业盟友",
                InteractionOptions = "商业交谈,送礼",
                PastExperience = "设计引诱各王选阵营齐聚普利斯特拉以图后效。"
            });
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "尤里乌斯",
                Gender = "男",
                Age = 21,
                BriefIntro = "被称为「最优秀骑士」的帅气青年",
                Appearance = "紫发英挺、身穿近卫骑士华装的男子",
                IdentityText = "近卫骑士团所属，准精使",
                BaseAttributes = "力量:80; 敏捷:85; 智力:85; 意志:90",
                SpecialAttributes = "诱精加护:六属准精契约",
                LocationName = protagonist.LocationName,
                RelationsText = $"{protagonist.Name}:挚友",
                InteractionOptions = "切磋,交谈",
                PastExperience = "协助安娜塔西亚打理水门都市的安全事务。"
            });
            npcs.Add(new ImportantNpc
            {
                RowId = rowId++,
                Name = "雷格鲁斯",
                Gender = "男",
                Age = 100,
                BriefIntro = "魔女教强欲大罪司教",
                Appearance = "白发白衣、面容普通的年青男子",
                IdentityText = "拥有无敌权能的自我中心主义狂魔",
                BaseAttributes = "力量:99; 敏捷:99; 智力:50; 意志:90",
                SpecialAttributes = "狮子的心脏:时间停滞; 小国王:无敌范围",
                LocationName = "水门都市深处",
                RelationsText = $"{protagonist.Name}:敌对",
                InteractionOptions = "激怒,开战",
                PastExperience = "正准备突袭水门都市夺取自己的「新娘」。"
            });
        }

        if (npcs.Count > 0)
        {
            dbContext.ImportantNpcs.AddRange(npcs);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}

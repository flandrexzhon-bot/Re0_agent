using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;

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

    /// <summary>菜月昴的稳定身份 ID，与世界书条目内嵌的 CharId 一致。</summary>
    private const int SubaruCharId = 4;

    /// <summary>菜月昴初始技能（来自世界书 &lt;角色属性&gt; 卡）。</summary>
    private static readonly string SubaruSkillsJson = SkillsSerializer.Serialize(
    [
        new CharacterSkill { Name = "全力冲刺", ManaCost = 0, StaminaCost = 40, Description = "不顾一切地向指定方向奔跑。无法用于攻击或闪避，单纯的逃跑动作", Available = true },
        new CharacterSkill { Name = "虚张声势", ManaCost = 0, StaminaCost = 5, Description = "摆出夸张的姿势并大声说话，试图威吓敌人或吸引注意。通常没有实际效果，反而可能吸引更多危险", Available = true }
    ]);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task EnsureDefaultTemplateAsync(CancellationToken cancellationToken = default)
    {
        await DatabaseInitializer.InitializeAsync(dbContext, cancellationToken);

        var baseData = JsonSerializer.Serialize(CreateDefaultSubaruProtagonist(), JsonOptions);

        var existing = await dbContext.ProtagonistTemplates
            .FirstOrDefaultAsync(template => template.TemplateName == SubaruName, cancellationToken);
        if (existing is not null)
        {
            // 内置默认模板由代码定义，始终刷新为最新预设（六维属性/HP/技能等）。
            if (existing.IsDefault == 1 && !string.Equals(existing.BaseData, baseData, StringComparison.Ordinal))
            {
                existing.BaseData = baseData;
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            return;
        }

        dbContext.ProtagonistTemplates.Add(new ProtagonistTemplate
        {
            TemplateName = SubaruName,
            IncludesSubaru = 0,
            BaseData = baseData,
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

        // 不再硬编码初始地点：留空，由填表 Agent 在首回合按世界书生成。
        protagonist.LocationName = string.Empty;
        protagonist = GameStateTextNormalizer.NormalizeProtagonist(protagonist);

        var addedSubaruNpc = false;
        var transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            dbContext.ChangeTracker.Clear();

            await EnsureMinimumWorldStateAsync(protagonist, startingChapter, cancellationToken);

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

        var cleanProtagonist = GameStateTextNormalizer.NormalizeProtagonist(protagonist, "王都");
        cleanProtagonist.RowId = 1;

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

        dbContext.GlobalStates.Add(new GlobalState
        {
            RowId = 1,
            CurrentLocation = string.Empty,
            CurrentMinorRegion = string.Empty,
            CurrentMajorRegion = string.Empty,
            ElapsedTime = "0分钟",
            CurTime = GetInitialCurTime(startingChapter),
            CurrentChapter = startingChapter,
            IsLewd = "否"
        });

        // 不再硬编码任何初始地点：world_map_points 与 global_state 地点字段均留空，
        // 由填表 Agent 在首回合按世界书生成（含主角所在地点）。
    }

    private static string GetInitialCurTime(int startingChapter)
    {
        return startingChapter switch
        {
            // 世界书存档点给出塔姆兹月14日；具体时分留给玩家自定义。
            1 => "塔姆兹月-14日-??:??",
            // 世界书存档点给出塔姆兹月15日；具体时分留给玩家自定义。
            7 => "塔姆兹月-15日-??:??",
            // 世界书存档点给出塔姆兹月25日；具体时分留给玩家自定义。
            18 => "塔姆兹月-25日-??:??",
            // 世界书未给出复活点月日或具体钟点。
            53 => "未知月-未知日-??:??",
            // 世界书存档点给出第二年塔姆兹月3日；具体时分留给玩家自定义。
            82 => "第二年塔姆兹月-3日-??:??",
            _ => "未知月-未知日-??:??"
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
            CharId = SubaruCharId,
            Name = SubaruName,
            Gender = "男",
            Age = 17,
            BriefIntro = "黑发黑眼的异世界少年",
            Appearance = "黑发黑眼，穿运动服的少年",
            IdentityText = "异世界来客",
            BaseAttributes = "力量:12; 敏捷:14; 耐力:10; 智力:11; 精神:8; 魅力:9",
            SpecialAttributes = "死亡回归:特殊",
            LocationName = protagonist.LocationName,
            RelationsText = $"{protagonist.Name}:同行",
            InteractionOptions = "交谈,同行",
            PastExperience = "作为异世界来客卷入当前事件。",
            Hp = 100,
            MaxHp = 100,
            Mp = 5,
            MaxMp = 5,
            Stamina = 104,
            MaxStamina = 104,
            Armor = 0,
            SkillsJson = SubaruSkillsJson
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
            CharId = SubaruCharId,
            Name = SubaruName,
            Gender = "男",
            Age = 17,
            Appearance = "黑发黑眼，穿运动服的少年",
            IdentityText = "异世界来客",
            SelfStatus = "正常",
            LocationName = string.Empty,
            BaseAttributes = "力量:12; 敏捷:14; 耐力:10; 智力:11; 精神:8; 魅力:9",
            SpecialAttributes = "死亡回归:特殊",
            ResourcesText = string.Empty,
            Hp = 100,
            MaxHp = 100,
            Mp = 5,
            MaxMp = 5,
            Stamina = 104,
            MaxStamina = 104,
            Armor = 0,
            SkillsJson = SubaruSkillsJson
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

}

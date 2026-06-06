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
            2 => "罗兹瓦尔宅邸",
            3 => "罗兹瓦尔宅邸",
            4 => "圣域",
            5 => "水门都市",
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
            PresenceStatus = "在场",
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
}

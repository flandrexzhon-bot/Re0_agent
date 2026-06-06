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
        CancellationToken cancellationToken = default)
    {
        await EnsureDefaultTemplateAsync(cancellationToken);

        var template = await dbContext.ProtagonistTemplates.AsNoTracking()
            .FirstOrDefaultAsync(item => item.TemplateId == templateId, cancellationToken)
            ?? throw new InvalidOperationException("找不到指定主角模板。");

        var protagonist = ReadTemplateProtagonist(template.BaseData);
        protagonist.RowId = 1;

        var addedSubaruNpc = false;
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            dbContext.ChangeTracker.Clear();

            await EnsureMinimumWorldStateAsync(protagonist, cancellationToken);
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
            await transaction.CommitAsync(cancellationToken);
        }

        var savePoint = await saveSystem.CreateSavePointAsync("initial_template", cancellationToken);
        return new TemplateApplyResult(
            template.TemplateId,
            template.TemplateName,
            protagonist.Name,
            addedSubaruNpc,
            savePoint.SaveId);
    }

    public async Task ResetGameAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            dbContext.ChangeTracker.Clear();
            
            await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM global_state;", cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM world_map_points;", cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM map_elements;", cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM factions;", cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM protagonist_info;", cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM important_npc;", cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM inventory;", cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM equipment;", cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM quests;", cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM chronicle;", cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM character_memory;", cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM death_return_log;", cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM save_points;", cancellationToken);
            
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        var defaultTemplate = await dbContext.ProtagonistTemplates.FirstOrDefaultAsync(t => t.IsDefault == 1, cancellationToken);
        if (defaultTemplate is null)
        {
            await EnsureDefaultTemplateAsync(cancellationToken);
            defaultTemplate = await dbContext.ProtagonistTemplates.FirstAsync(t => t.IsDefault == 1, cancellationToken);
        }

        await ApplyTemplateAsync(defaultTemplate.TemplateId, cancellationToken);
    }

    private async Task EnsureMinimumWorldStateAsync(
        ProtagonistInfo protagonist,
        CancellationToken cancellationToken)
    {
        if (!await dbContext.GlobalStates.AnyAsync(cancellationToken))
        {
            dbContext.GlobalStates.Add(new GlobalState
            {
                RowId = 1,
                CurrentLocation = protagonist.LocationName,
                CurrentMinorRegion = "王都中心",
                CurrentMajorRegion = "露格尼卡",
                ElapsedTime = "0分钟",
                CurTime = "2024-04-01 09:00",
                CurrentChapter = 1,
                IsLewd = "否"
            });
        }

        if (!await dbContext.WorldMapPoints.AnyAsync(point => point.LocationName == protagonist.LocationName, cancellationToken))
        {
            dbContext.WorldMapPoints.Add(new WorldMapPoint
            {
                RowId = await NextRowIdAsync(dbContext.WorldMapPoints, cancellationToken),
                LocationName = protagonist.LocationName,
                MinorRegion = "王都中心",
                MajorRegion = "露格尼卡",
                LocationType = "特殊",
                EnvironmentDesc = "主角模板初始化地点",
                Importance = "核心",
                ExplorationStatus = "部分探索"
            });
        }
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

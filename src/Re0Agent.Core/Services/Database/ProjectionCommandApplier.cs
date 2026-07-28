using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;

namespace Re0Agent.Core.Services.Database;

public sealed class ProjectionCommandApplier(Re0AgentDbContext dbContext)
{
    private static readonly IReadOnlyDictionary<string, Type> ProjectionTypes = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["global_state"] = typeof(GlobalState),
        ["world_map_points"] = typeof(WorldMapPoint),
        ["map_elements"] = typeof(MapElement),
        ["factions"] = typeof(Faction),
        ["protagonist_info"] = typeof(ProtagonistInfo),
        ["important_npc"] = typeof(ImportantNpc),
        ["inventory"] = typeof(InventoryItem),
        ["equipment"] = typeof(EquipmentItem),
        ["quests"] = typeof(Quest),
        ["chronicle"] = typeof(ChronicleEntry),
        ["character_memory"] = typeof(CharacterMemory),
        ["scene_states"] = typeof(SceneState),
        ["character_agency_states"] = typeof(CharacterAgencyState),
        ["story_threads"] = typeof(StoryThread)
    };

    public async Task ApplyAsync(
        string eventId,
        StateChangeSet stateChangeSet,
        CancellationToken cancellationToken = default)
    {
        foreach (var command in stateChangeSet.Commands)
        {
            if (await dbContext.ProjectionCommandLogs.AnyAsync(item => item.CommandId == command.CommandId, cancellationToken))
            {
                continue;
            }

            var entityType = ProjectionTypes[command.Projection];
            var entityMetadata = dbContext.Model.FindEntityType(entityType)
                ?? throw new InvalidOperationException("找不到投影实体元数据。");
            var key = entityMetadata.FindPrimaryKey()
                ?? throw new InvalidOperationException("投影实体缺少主键。");
            if (key.Properties.Count != 1)
            {
                throw new InvalidOperationException("当前只支持单键投影命令。");
            }

            var version = await dbContext.ProjectionEntityVersions.SingleOrDefaultAsync(
                item => item.Projection == command.Projection && item.EntityId == command.EntityId,
                cancellationToken);
            var actualVersion = version?.Version ?? 0;
            if (command.ExpectedVersion is not null && command.ExpectedVersion != actualVersion)
            {
                throw new InvalidOperationException($"投影 {command.Projection}/{command.EntityId} 版本冲突。");
            }

            await ApplyCommandAsync(command, entityType, entityMetadata, key.Properties[0], cancellationToken);
            if (version is null)
            {
                version = new ProjectionEntityVersion
                {
                    Projection = command.Projection,
                    EntityId = command.EntityId,
                    Version = 1
                };
                dbContext.ProjectionEntityVersions.Add(version);
            }
            else
            {
                version.Version++;
            }

            dbContext.ProjectionCommandLogs.Add(new ProjectionCommandLog
            {
                CommandId = command.CommandId,
                EventId = eventId,
                AppliedAt = DateTimeOffset.UtcNow.ToString("O")
            });
        }
    }

    private async Task ApplyCommandAsync(
        StateChangeCommand command,
        Type entityType,
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entityMetadata,
        Microsoft.EntityFrameworkCore.Metadata.IProperty primaryKey,
        CancellationToken cancellationToken)
    {
        var automaticInsert = command.OperationType == "insert" && command.EntityId.StartsWith("auto:", StringComparison.Ordinal);
        var keyValue = automaticInsert ? null : ConvertValue(command.EntityId, primaryKey.ClrType);
        var entity = automaticInsert ? null : await dbContext.FindAsync(entityType, [keyValue!], cancellationToken);
        switch (command.OperationType)
        {
            case "insert":
                if (entity is not null) throw new InvalidOperationException("插入目标已存在。");
                entity = Activator.CreateInstance(entityType) ?? throw new InvalidOperationException("无法创建投影实体。");
                if (!automaticInsert) SetProperty(entity, primaryKey.PropertyInfo!, keyValue);
                ApplyFields(entity, entityMetadata, command.FieldChanges);
                dbContext.Add(entity);
                break;
            case "update":
                if (entity is null) throw new InvalidOperationException("更新目标不存在。");
                ApplyFields(entity, entityMetadata, command.FieldChanges);
                break;
            case "delete":
                if (entity is null) throw new InvalidOperationException("删除目标不存在。");
                dbContext.Remove(entity);
                break;
            default:
                throw new InvalidOperationException("未知的投影操作。");
        }
    }

    private static void ApplyFields(
        object entity,
        Microsoft.EntityFrameworkCore.Metadata.IEntityType entityMetadata,
        IReadOnlyDictionary<string, JsonElement> fields)
    {
        foreach (var (columnName, value) in fields)
        {
            var property = entityMetadata.GetProperties().SingleOrDefault(item => item.GetColumnName() == columnName);
            if (property?.PropertyInfo is null || property.IsPrimaryKey())
            {
                throw new InvalidOperationException($"字段 {columnName} 不可写。");
            }
            SetProperty(entity, property.PropertyInfo, ConvertJsonValue(value, property.ClrType));
        }
    }

    private static void SetProperty(object entity, PropertyInfo property, object? value) => property.SetValue(entity, value);

    private static object? ConvertJsonValue(JsonElement value, Type targetType)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return Nullable.GetUnderlyingType(targetType) is not null || !targetType.IsValueType ? null
                : throw new InvalidOperationException("非空字段不能写入 null。");
        }
        return JsonSerializer.Deserialize(value.GetRawText(), targetType)
            ?? throw new InvalidOperationException("字段值无法转换。");
    }

    private static object ConvertValue(string value, Type targetType) =>
        Convert.ChangeType(value, Nullable.GetUnderlyingType(targetType) ?? targetType, CultureInfo.InvariantCulture);
}

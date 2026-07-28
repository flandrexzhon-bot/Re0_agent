using System.Text.Json;
using Re0Agent.Core.Models;

namespace Re0Agent.Core.Services.Database;

public sealed class StateChangeSetValidator
{
    private static readonly HashSet<string> AllowedProjections = new(StringComparer.Ordinal)
    {
        "global_state", "world_map_points", "map_elements", "factions", "protagonist_info",
        "important_npc", "inventory", "equipment", "quests", "chronicle", "character_memory",
        "scene_states", "character_agency_states", "story_threads"
    };

    private static readonly HashSet<string> AllowedOperations = new(StringComparer.Ordinal)
    {
        "insert", "update", "delete"
    };

    public StateChangeSet Validate(StateChangeSet proposal)
    {
        var commandIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in proposal.Commands)
        {
            if (string.IsNullOrWhiteSpace(command.CommandId) || !commandIds.Add(command.CommandId))
            {
                throw new InvalidOperationException("StateChangeSet 包含空或重复 command_id。");
            }
            if (!AllowedProjections.Contains(command.Projection) || !AllowedOperations.Contains(command.OperationType))
            {
                throw new InvalidOperationException("StateChangeSet 包含不允许的投影或操作。");
            }
            if (string.IsNullOrWhiteSpace(command.EntityId) || command.FieldChanges is null)
            {
                throw new InvalidOperationException("StateChangeSet 缺少实体 ID 或字段变更。");
            }
            if (command.FieldChanges.Values.Any(value => value.ValueKind is JsonValueKind.Undefined))
            {
                throw new InvalidOperationException("StateChangeSet 包含未定义字段值。");
            }
        }

        return proposal;
    }
}

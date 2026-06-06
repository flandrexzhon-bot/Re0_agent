using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Re0Agent.Core.Entities;

[Table("character_memory")]
public sealed class CharacterMemory
{
    [Key]
    [Column("row_id")]
    public int RowId { get; set; }

    [Column("character_name")]
    public required string CharacterName { get; set; }

    [Column("round_index")]
    public required string RoundIndex { get; set; }

    [Column("memory_text")]
    public required string MemoryText { get; set; }

    [Column("emotional_state")]
    public string? EmotionalState { get; set; }

    [Column("created_at")]
    public required string CreatedAt { get; set; }
}

[Table("save_points")]
public sealed class SavePoint
{
    [Key]
    [Column("save_id")]
    public int SaveId { get; set; }

    [Column("chapter")]
    public int Chapter { get; set; }

    [Column("trigger_reason")]
    public required string TriggerReason { get; set; }

    [Column("global_state_snapshot")]
    public required string GlobalStateSnapshot { get; set; }

    [Column("protagonist_snapshot")]
    public required string ProtagonistSnapshot { get; set; }

    [Column("world_map_snapshot")]
    public required string WorldMapSnapshot { get; set; }

    [Column("map_elements_snapshot")]
    public required string MapElementsSnapshot { get; set; }

    [Column("factions_snapshot")]
    public required string FactionsSnapshot { get; set; }

    [Column("npc_snapshot")]
    public required string NpcSnapshot { get; set; }

    [Column("inventory_snapshot")]
    public required string InventorySnapshot { get; set; }

    [Column("equipment_snapshot")]
    public required string EquipmentSnapshot { get; set; }

    [Column("quest_snapshot")]
    public required string QuestSnapshot { get; set; }

    [Column("created_at")]
    public required string CreatedAt { get; set; }
}

[Table("death_return_log")]
public sealed class DeathReturnLog
{
    [Key]
    [Column("log_id")]
    public int LogId { get; set; }

    [Column("loop_count")]
    public int LoopCount { get; set; }

    [Column("death_cause")]
    public required string DeathCause { get; set; }

    [Column("miasma_level")]
    public int MiasmaLevel { get; set; }

    [Column("save_point_id")]
    public int? SavePointId { get; set; }

    [Column("chronicle_index")]
    public string? ChronicleIndex { get; set; }

    [Column("created_at")]
    public required string CreatedAt { get; set; }
}

[Table("agent_config")]
public sealed class AgentConfig
{
    [Key]
    [Column("config_id")]
    public int ConfigId { get; set; }

    [Column("agent_type")]
    public required string AgentType { get; set; }

    [Column("agent_name")]
    public required string AgentName { get; set; }

    [Column("api_endpoint")]
    public required string ApiEndpoint { get; set; }

    [Column("api_key")]
    public required string ApiKey { get; set; }

    [Column("model_name")]
    public required string ModelName { get; set; }

    [Column("temperature")]
    public double Temperature { get; set; } = 0.7;

    [Column("max_tokens")]
    public int MaxTokens { get; set; } = 4096;

    [Column("system_prompt")]
    public string? SystemPrompt { get; set; }

    [Column("enabled")]
    public int Enabled { get; set; } = 1;
}

[Table("protagonist_templates")]
public sealed class ProtagonistTemplate
{
    [Key]
    [Column("template_id")]
    public int TemplateId { get; set; }

    [Column("template_name")]
    public required string TemplateName { get; set; }

    [Column("includes_subaru")]
    public int IncludesSubaru { get; set; } = 1;

    [Column("base_data")]
    public required string BaseData { get; set; }

    [Column("is_default")]
    public int IsDefault { get; set; }
}

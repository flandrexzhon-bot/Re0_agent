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

    /// <summary>编年史快照。仅 fork/重 roll 的「平行时间线」存档写入并回滚；
    /// 死亡回归/旧存档为 null —— 此时 restore 不动 chronicle（append-only 元历史）。</summary>
    [Column("chronicle_snapshot")]
    public string? ChronicleSnapshot { get; set; }

    /// <summary>角色记忆快照。语义同 <see cref="ChronicleSnapshot"/>：仅平行时间线存档回滚。</summary>
    [Column("character_memory_snapshot")]
    public string? CharacterMemorySnapshot { get; set; }

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

    [Column("max_input_tokens")]
    public int MaxInputTokens { get; set; } = 4096;

    /// <summary>是否启用思考模式（DeepSeek 等推理模型，OpenAI 格式 reasoning）。</summary>
    [Column("enable_thinking")]
    public bool EnableThinking { get; set; } = false;

    /// <summary>思考强度（OpenAI 格式 reasoning_effort）：low / medium / high。</summary>
    [Column("reasoning_effort")]
    public string ReasoningEffort { get; set; } = "medium";

    /// <summary>当输出为空或报错时自动重复请求（默认启用）。</summary>
    [Column("auto_retry")]
    public bool AutoRetry { get; set; } = true;
}

[Table("api_routing")]
public sealed class ApiRouting
{
    [Key]
    [Column("routing_key")]
    public required string RoutingKey { get; set; }

    [Column("preset_name")]
    public required string PresetName { get; set; }
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

[Table("chat_sessions")]
public sealed class ChatSession
{
    [Key]
    [Column("session_id")]
    public int SessionId { get; set; }

    [Column("session_name")]
    public required string SessionName { get; set; }

    [Column("is_active")]
    public int IsActive { get; set; } = 0;

    /// <summary>分支来源会话 ID（SillyTavern 式 branch）。null 表示根会话（非分支而来）。</summary>
    [Column("parent_session_id")]
    public int? ParentSessionId { get; set; }

    [Column("created_at")]
    public required string CreatedAt { get; set; }

    [Column("global_state_snapshot")]
    public string GlobalStateSnapshot { get; set; } = "{}";

    [Column("protagonist_snapshot")]
    public string ProtagonistSnapshot { get; set; } = "{}";

    [Column("world_map_snapshot")]
    public string WorldMapSnapshot { get; set; } = "[]";

    [Column("map_elements_snapshot")]
    public string MapElementsSnapshot { get; set; } = "[]";

    [Column("factions_snapshot")]
    public string FactionsSnapshot { get; set; } = "[]";

    [Column("npc_snapshot")]
    public string NpcSnapshot { get; set; } = "[]";

    [Column("inventory_snapshot")]
    public string InventorySnapshot { get; set; } = "[]";

    [Column("equipment_snapshot")]
    public string EquipmentSnapshot { get; set; } = "[]";

    [Column("quest_snapshot")]
    public string QuestSnapshot { get; set; } = "[]";

    [Column("chronicle_snapshot")]
    public string ChronicleSnapshot { get; set; } = "[]";

    [Column("character_memory_snapshot")]
    public string CharacterMemorySnapshot { get; set; } = "[]";

    [Column("death_return_log_snapshot")]
    public string DeathReturnLogSnapshot { get; set; } = "[]";

    [Column("save_points_snapshot")]
    public string SavePointsSnapshot { get; set; } = "[]";

    [Column("detailed_rounds_snapshot")]
    public string DetailedRoundsSnapshot { get; set; } = "[]";

    /// <summary>各回合的重 roll 变体集合（JSON：Dictionary&lt;RoundIndex, RoundVariantSet&gt;）。
    /// 长期保留每个回合的 roll 记录；前端只在最新回合显示 swipe/重 roll，fork 回到某回合即可重现。</summary>
    [Column("round_variants_snapshot")]
    public string RoundVariantsSnapshot { get; set; } = "{}";

    /// <summary>当前回合状态机段位（RoundPhase.ToString()）。算法化持久，重开 App 不靠猜。</summary>
    [Column("current_round_phase")]
    public string CurrentRoundPhase { get; set; } = "Idle";

    /// <summary>停止时所处的段编号（1–6）；用于「继续」精确从断点接着跑。0 表示无断点。</summary>
    [Column("interrupted_step")]
    public int InterruptedStep { get; set; } = 0;
}

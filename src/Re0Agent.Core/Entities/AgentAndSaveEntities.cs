using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Re0Agent.Core.Entities;

[Table("character_memory")]
public sealed class CharacterMemory
{
    [Key]
    [Column("row_id")]
    public int RowId { get; set; }

    [Column("owner_character_id")]
    public required string OwnerCharacterId { get; set; }

    [Column("source_event_id")]
    public required string SourceEventId { get; set; }

    [Column("world_time")]
    public required string WorldTime { get; set; }

    [Column("world_epoch")]
    public int WorldEpoch { get; set; }

    [Column("observation_channel")]
    public string? ObservationChannel { get; set; }

    [Column("confidence")]
    public required string Confidence { get; set; } = "确知";

    [Column("visibility_scope")]
    public required string VisibilityScope { get; set; }

    [Column("memory_type")]
    public required string MemoryType { get; set; }

    [Column("retain_on_rewind")]
    public int RetainOnRewind { get; set; }

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

    [Column("branch_id")]
    public required string BranchId { get; set; }

    [Column("event_id")]
    public required string EventId { get; set; }

    [Column("world_epoch")]
    public int WorldEpoch { get; set; }

    [Column("trigger_reason")]
    public required string TriggerReason { get; set; }

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
    [Column("created_at")]
    public required string CreatedAt { get; set; }

    [Column("game_mode")]
    public required string GameMode { get; set; } = "RP";

    [Column("session_time_scale")]
    public double SessionTimeScale { get; set; } = 1.0;

    [Column("input_slow_factor")]
    public double InputSlowFactor { get; set; } = 1.0;

    [Column("world_clock_anchor")]
    public string? WorldClockAnchor { get; set; }

    [Column("is_paused")]
    public int IsPaused { get; set; }

    [Column("current_branch_id")]
    public string? CurrentBranchId { get; set; }

    [Column("current_world_epoch")]
    public int CurrentWorldEpoch { get; set; } = 1;
}

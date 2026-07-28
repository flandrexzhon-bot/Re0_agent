using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Re0Agent.Core.Entities;

[Table("timeline_branches")]
public sealed class TimelineBranch
{
    [Key]
    [Column("branch_id")]
    public required string BranchId { get; set; }

    [Column("session_id")]
    public int SessionId { get; set; }

    [Column("parent_branch_id")]
    public string? ParentBranchId { get; set; }

    [Column("parent_event_id")]
    public string? ParentEventId { get; set; }

    [Column("branch_reason")]
    public required string BranchReason { get; set; }

    [Column("created_at")]
    public required string CreatedAt { get; set; }
}

[Table("timeline_events")]
public sealed class TimelineEvent
{
    [Key]
    [Column("event_id")]
    public required string EventId { get; set; }

    [Column("branch_id")]
    public required string BranchId { get; set; }

    [Column("parent_event_id")]
    public string? ParentEventId { get; set; }

    [Column("sequence")]
    public long? Sequence { get; set; }

    [Column("world_epoch")]
    public int WorldEpoch { get; set; }

    [Column("scene_id")]
    public string? SceneId { get; set; }

    [Column("actor_id")]
    public string? ActorId { get; set; }

    [Column("target_id")]
    public string? TargetId { get; set; }

    [Column("event_type")]
    public required string EventType { get; set; }

    [Column("content")]
    public required string Content { get; set; }

    [Column("state_change_set")]
    public string? StateChangeSet { get; set; }

    [Column("direct_observers")]
    public required string DirectObservers { get; set; } = "[]";

    [Column("potential_learners")]
    public required string PotentialLearners { get; set; } = "[]";

    [Column("observability_computed")]
    public int ObservabilityComputed { get; set; }

    [Column("visibility_scope")]
    public required string VisibilityScope { get; set; } = "[]";

    [Column("revealed_event_cursors")]
    public required string RevealedEventCursors { get; set; } = "[]";

    [Column("status")]
    public required string Status { get; set; } = "Draft";

    [Column("pacing_metadata")]
    public string? PacingMetadata { get; set; }

    [Column("trigger_cause")]
    public string? TriggerCause { get; set; }

    [Column("causal_parent_event_id")]
    public string? CausalParentEventId { get; set; }

    [Column("real_created_at")]
    public required string RealCreatedAt { get; set; }

    [Column("world_time")]
    public required string WorldTime { get; set; }
}

[Table("projection_checkpoints")]
public sealed class ProjectionCheckpoint
{
    [Key]
    [Column("checkpoint_id")]
    public int CheckpointId { get; set; }

    [Column("branch_id")]
    public required string BranchId { get; set; }

    [Column("event_sequence")]
    public long EventSequence { get; set; }

    [Column("world_epoch")]
    public int WorldEpoch { get; set; }

    [Column("projection_data")]
    public required string ProjectionData { get; set; }

    [Column("created_at")]
    public required string CreatedAt { get; set; }
}

[Table("projection_entity_versions")]
public sealed class ProjectionEntityVersion
{
    [Key]
    [Column("version_id")]
    public int VersionId { get; set; }

    [Column("projection")]
    public required string Projection { get; set; }

    [Column("entity_id")]
    public required string EntityId { get; set; }

    [Column("version")]
    public long Version { get; set; }
}

[Table("projection_command_log")]
public sealed class ProjectionCommandLog
{
    [Key]
    [Column("command_id")]
    public required string CommandId { get; set; }

    [Column("event_id")]
    public required string EventId { get; set; }

    [Column("applied_at")]
    public required string AppliedAt { get; set; }
}

[Table("world_scheduler_jobs")]
public sealed class WorldSchedulerJob
{
    [Key]
    [Column("job_id")]
    public int JobId { get; set; }

    [Column("session_id")]
    public int SessionId { get; set; }

    [Column("job_type")]
    public required string JobType { get; set; }

    [Column("scheduled_world_time")]
    public required string ScheduledWorldTime { get; set; }

    [Column("payload")]
    public string? Payload { get; set; }

    [Column("status")]
    public required string Status { get; set; } = "Pending";

    [Column("created_at")]
    public required string CreatedAt { get; set; }
}

[Table("world_runtime_state")]
public sealed class WorldRuntimeState
{
    [Key]
    [Column("session_id")]
    public int SessionId { get; set; }

    [Column("generation_backpressure_factor")]
    public double GenerationBackpressureFactor { get; set; } = 1.0;

    [Column("foreground_pending_count")]
    public int ForegroundPendingCount { get; set; }

    [Column("foreground_lag_seconds")]
    public double ForegroundLagSeconds { get; set; }

    [Column("current_scene_budget_used")]
    public int CurrentSceneBudgetUsed { get; set; }

    [Column("input_activity_started_at")]
    public string? InputActivityStartedAt { get; set; }

    [Column("input_event_count")]
    public int InputEventCount { get; set; }

    [Column("foreground_admission_limit")]
    public int ForegroundAdmissionLimit { get; set; } = 1;

    [Column("budget_window_started_at")]
    public required string BudgetWindowStartedAt { get; set; }

    [Column("updated_at")]
    public required string UpdatedAt { get; set; }
}

[Table("pending_directions")]
public sealed class PendingDirection
{
    [Key]
    [Column("direction_id")]
    public int DirectionId { get; set; }
    [Column("session_id")] public int SessionId { get; set; }
    [Column("source_event_id")] public required string SourceEventId { get; set; }
    [Column("content")] public required string Content { get; set; }
    [Column("precondition_chain")] public required string Preconditions { get; set; } = "[]";
    [Column("earliest_world_time")] public required string EarliestWorldTime { get; set; }
    [Column("completion_progress")] public double CompletionProgress { get; set; }
    [Column("block_reason")] public string? BlockReason { get; set; }
    [Column("status")] public required string Status { get; set; } = "Pending";
    [Column("created_at")] public required string CreatedAt { get; set; }
}

[Table("reveal_queue")]
public sealed class RevealQueueItem
{
    [Key]
    [Column("queue_id")] public int QueueId { get; set; }
    [Column("session_id")] public int SessionId { get; set; }
    [Column("event_id")] public required string EventId { get; set; }
    [Column("status")] public required string Status { get; set; } = "Pending";
    [Column("occurred_world_time")] public required string OccurredWorldTime { get; set; }
    [Column("importance")] public double Importance { get; set; }
    [Column("story_thread_id")] public int? StoryThreadId { get; set; }
    [Column("allowed_visibility_scope")] public required string AllowedVisibilityScope { get; set; } = "[]";
    [Column("causal_distance")] public int CausalDistance { get; set; }
    [Column("latest_reveal_world_time")] public required string LatestRevealWorldTime { get; set; }
    [Column("must_reveal")] public int MustReveal { get; set; }
    [Column("coalesced_event_ids")] public required string CoalescedEventIds { get; set; } = "[]";
    [Column("merge_category")] public string MergeCategory { get; set; } = "other";
    [Column("created_at")] public required string CreatedAt { get; set; }
}

[Table("lorebook_condition_entries")]
public sealed class LorebookConditionEntry
{
    [Key]
    [Column("entry_id")]
    public int EntryId { get; set; }

    [Column("source_key")]
    public required string SourceKey { get; set; }

    [Column("source_order")]
    public int SourceOrder { get; set; }

    [Column("legacy_condition")]
    public required string LegacyCondition { get; set; }

    [Column("fact_predicate")]
    public required string FactPredicate { get; set; }

    [Column("content")]
    public required string Content { get; set; }

    [Column("created_at")]
    public required string CreatedAt { get; set; }
}

[Table("character_card_sources")]
public sealed class CharacterCardSource
{
    [Key]
    [Column("source_id")] public int SourceId { get; set; }
    [Column("source_key")] public required string SourceKey { get; set; }
    [Column("format")] public required string Format { get; set; }
    [Column("name")] public required string Name { get; set; }
    [Column("description")] public string? Description { get; set; }
    [Column("personality")] public string? Personality { get; set; }
    [Column("scenario")] public string? Scenario { get; set; }
    [Column("system_prompt")] public string? SystemPrompt { get; set; }
    [Column("post_history_instructions")] public string? PostHistoryInstructions { get; set; }
    [Column("example_dialogues")] public string? ExampleDialogues { get; set; }
    [Column("creator")] public string? Creator { get; set; }
    [Column("character_version")] public string? CharacterVersion { get; set; }
    [Column("tags")] public string? Tags { get; set; }
    [Column("first_mes")] public string? FirstMessage { get; set; }
    [Column("alternate_greetings")] public required string AlternateGreetings { get; set; } = "[]";
    [Column("character_book")] public string? CharacterBook { get; set; }
    [Column("extensions")] public required string Extensions { get; set; } = "{}";
    [Column("created_at")] public required string CreatedAt { get; set; }
}

[Table("lorebook_sources")]
public sealed class LorebookSource
{
    [Key]
    [Column("source_id")] public int SourceId { get; set; }
    [Column("source_key")] public required string SourceKey { get; set; }
    [Column("format")] public required string Format { get; set; }
    [Column("entries")] public required string Entries { get; set; }
    [Column("extensions")] public required string Extensions { get; set; } = "{}";
    [Column("created_at")] public required string CreatedAt { get; set; }
}

[Table("scene_states")]
public sealed class SceneState
{
    [Key, Column("scene_id")] public required string SceneId { get; set; }
    [Column("region_id")] public string? RegionId { get; set; }
    [Column("is_foreground")] public int IsForeground { get; set; }
    [Column("summary")] public required string Summary { get; set; } = "{}";
    [Column("last_world_time")] public required string LastWorldTime { get; set; }
}

[Table("character_agency_states")]
public sealed class CharacterAgencyState
{
    [Key, Column("character_id")] public required string CharacterId { get; set; }
    [Column("scene_id")] public string? SceneId { get; set; }
    [Column("current_goal")] public string? CurrentGoal { get; set; }
    [Column("next_action_world_time")] public required string NextActionWorldTime { get; set; }
    [Column("fidelity")] public required string Fidelity { get; set; } = "foreground";
    [Column("status")] public required string Status { get; set; } = "Active";
}

[Table("story_threads")]
public sealed class StoryThread
{
    [Key, Column("thread_id")] public int ThreadId { get; set; }
    [Column("scope")] public required string Scope { get; set; }
    [Column("status")] public required string Status { get; set; } = "Active";
    [Column("urgency")] public double Urgency { get; set; }
    [Column("prerequisites")] public required string Prerequisites { get; set; } = "[]";
    [Column("updated_world_time")] public required string UpdatedWorldTime { get; set; }
    [Column("last_plan_version_id")] public int? LastPlanVersionId { get; set; }
    [Column("modified_count")] public int ModifiedCount { get; set; }
}

[Table("memory_embeddings")]
public sealed class MemoryEmbedding
{
    [Key, Column("memory_row_id")] public int MemoryRowId { get; set; }
    [Column("model_id")] public required string ModelId { get; set; }
    [Column("dimensions")] public int Dimensions { get; set; }
    [Column("world_epoch")] public int WorldEpoch { get; set; }
    [Column("vector_json")] public required string VectorJson { get; set; }
    [Column("content_hash")] public required string ContentHash { get; set; }
    [Column("created_at")] public required string CreatedAt { get; set; }
}

[Table("pacing_state_cache")]
public sealed class PacingStateCache
{
    [Key, Column("branch_id")] public required string BranchId { get; set; }
    [Column("event_id")] public required string EventId { get; set; }
    [Column("state_json")] public required string StateJson { get; set; }
    [Column("updated_at")] public required string UpdatedAt { get; set; }
}

[Table("director_plan_versions")]
public sealed class DirectorPlanVersion
{
    [Key, Column("version_id")] public int VersionId { get; set; }
    [Column("branch_id")] public required string BranchId { get; set; }
    [Column("source_event_id")] public required string SourceEventId { get; set; }
    [Column("plan_json")] public required string PlanJson { get; set; }
    [Column("reflection_reason")] public required string ReflectionReason { get; set; }
    [Column("changed_story_thread_id")] public int? ChangedStoryThreadId { get; set; }
    [Column("change_summary")] public required string ChangeSummary { get; set; } = "no_story_thread_change";
    [Column("pace_phase")] public string? PacePhase { get; set; }
    [Column("created_at")] public required string CreatedAt { get; set; }
}

[Table("director_pulses")]
public sealed class DirectorPulse
{
    [Key, Column("pulse_id")] public required string PulseId { get; set; }
    [Column("session_id")] public int SessionId { get; set; }
    [Column("trigger_event_id")] public required string TriggerEventId { get; set; }
    [Column("plan_version_id")] public int PlanVersionId { get; set; }
    [Column("status")] public required string Status { get; set; } = "Pending";
    [Column("suggestion_json")] public string? SuggestionJson { get; set; }
    [Column("baseline_json")] public string? BaselineJson { get; set; }
    [Column("is_shadow")] public int IsShadow { get; set; } = 1;
    [Column("benefit_score")] public double? BenefitScore { get; set; }
    [Column("expires_at")] public required string ExpiresAt { get; set; }
    [Column("created_at")] public required string CreatedAt { get; set; }
    [Column("completed_at")] public string? CompletedAt { get; set; }
    [Column("consumed_at")] public string? ConsumedAt { get; set; }
    [Column("error")] public string? Error { get; set; }
}

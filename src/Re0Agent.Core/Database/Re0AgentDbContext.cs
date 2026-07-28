using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Entities;

namespace Re0Agent.Core.Database;

public sealed class Re0AgentDbContext(DbContextOptions<Re0AgentDbContext> options) : DbContext(options)
{
    public DbSet<GlobalState> GlobalStates => Set<GlobalState>();
    public DbSet<WorldMapPoint> WorldMapPoints => Set<WorldMapPoint>();
    public DbSet<MapElement> MapElements => Set<MapElement>();
    public DbSet<Faction> Factions => Set<Faction>();
    public DbSet<ProtagonistInfo> ProtagonistInfo => Set<ProtagonistInfo>();
    public DbSet<ImportantNpc> ImportantNpcs => Set<ImportantNpc>();
    public DbSet<InventoryItem> Inventory => Set<InventoryItem>();
    public DbSet<EquipmentItem> Equipment => Set<EquipmentItem>();
    public DbSet<Quest> Quests => Set<Quest>();
    public DbSet<ChronicleEntry> Chronicle => Set<ChronicleEntry>();
    public DbSet<CharacterMemory> CharacterMemory => Set<CharacterMemory>();
    public DbSet<TimelineBranch> TimelineBranches => Set<TimelineBranch>();
    public DbSet<TimelineEvent> TimelineEvents => Set<TimelineEvent>();
    public DbSet<SavePoint> SavePoints => Set<SavePoint>();
    public DbSet<ProjectionCheckpoint> ProjectionCheckpoints => Set<ProjectionCheckpoint>();
    public DbSet<ProjectionEntityVersion> ProjectionEntityVersions => Set<ProjectionEntityVersion>();
    public DbSet<ProjectionCommandLog> ProjectionCommandLogs => Set<ProjectionCommandLog>();
    public DbSet<WorldSchedulerJob> WorldSchedulerJobs => Set<WorldSchedulerJob>();
    public DbSet<WorldRuntimeState> WorldRuntimeStates => Set<WorldRuntimeState>();
    public DbSet<PendingDirection> PendingDirections => Set<PendingDirection>();
    public DbSet<RevealQueueItem> RevealQueue => Set<RevealQueueItem>();
    public DbSet<LorebookConditionEntry> LorebookConditionEntries => Set<LorebookConditionEntry>();
    public DbSet<CharacterCardSource> CharacterCardSources => Set<CharacterCardSource>();
    public DbSet<LorebookSource> LorebookSources => Set<LorebookSource>();
    public DbSet<SceneState> SceneStates => Set<SceneState>();
    public DbSet<CharacterAgencyState> CharacterAgencyStates => Set<CharacterAgencyState>();
    public DbSet<StoryThread> StoryThreads => Set<StoryThread>();
    public DbSet<MemoryEmbedding> MemoryEmbeddings => Set<MemoryEmbedding>();
    public DbSet<PacingStateCache> PacingStateCache => Set<PacingStateCache>();
    public DbSet<DirectorPlanVersion> DirectorPlanVersions => Set<DirectorPlanVersion>();
    public DbSet<DeathReturnLog> DeathReturnLog => Set<DeathReturnLog>();
    public DbSet<AgentConfig> AgentConfig => Set<AgentConfig>();
    public DbSet<ProtagonistTemplate> ProtagonistTemplates => Set<ProtagonistTemplate>();
    public DbSet<ApiRouting> ApiRoutings => Set<ApiRouting>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DeathReturnLog>()
            .HasOne<SavePoint>()
            .WithMany()
            .HasForeignKey(log => log.SavePointId);

        modelBuilder.Entity<TimelineEvent>()
            .HasIndex(item => new { item.BranchId, item.Sequence })
            .IsUnique();

        modelBuilder.Entity<ProjectionCheckpoint>()
            .HasIndex(item => new { item.BranchId, item.EventSequence, item.WorldEpoch })
            .IsUnique();

        modelBuilder.Entity<LorebookConditionEntry>()
            .HasIndex(item => new { item.SourceKey, item.SourceOrder, item.LegacyCondition, item.Content })
            .IsUnique();
    }
}

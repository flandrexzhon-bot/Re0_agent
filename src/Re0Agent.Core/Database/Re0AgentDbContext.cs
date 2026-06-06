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
    public DbSet<SavePoint> SavePoints => Set<SavePoint>();
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
    }
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Re0Agent.Core.Entities;

[Table("global_state")]
public sealed class GlobalState
{
    [Key]
    [Column("row_id")]
    public int RowId { get; set; }

    [Column("current_location")]
    public required string CurrentLocation { get; set; }

    [Column("current_minor_region")]
    public required string CurrentMinorRegion { get; set; }

    [Column("current_major_region")]
    public required string CurrentMajorRegion { get; set; }

    [Column("prev_scene_time")]
    public string? PrevSceneTime { get; set; }

    [Column("elapsed_time")]
    public required string ElapsedTime { get; set; }

    [Column("cur_time")]
    public required string CurTime { get; set; }

    [Column("current_chapter")]
    public int CurrentChapter { get; set; } = 1;

    [Column("is_lewd")]
    public required string IsLewd { get; set; }
}

[Table("world_map_points")]
public sealed class WorldMapPoint
{
    [Key]
    [Column("row_id")]
    public int RowId { get; set; }

    [Column("location_name")]
    public required string LocationName { get; set; }

    [Column("minor_region")]
    public required string MinorRegion { get; set; }

    [Column("major_region")]
    public required string MajorRegion { get; set; }

    [Column("location_type")]
    public required string LocationType { get; set; }

    [Column("environment_desc")]
    public required string EnvironmentDesc { get; set; }

    [Column("importance")]
    public required string Importance { get; set; }

    [Column("exploration_status")]
    public required string ExplorationStatus { get; set; }
}

[Table("map_elements")]
public sealed class MapElement
{
    [Key]
    [Column("row_id")]
    public int RowId { get; set; }

    [Column("element_name")]
    public required string ElementName { get; set; }

    [Column("element_type")]
    public required string ElementType { get; set; }

    [Column("location_name")]
    public required string LocationName { get; set; }

    [Column("element_desc")]
    public required string ElementDesc { get; set; }

    [Column("status_text")]
    public required string StatusText { get; set; }

    [Column("interaction_options")]
    public required string InteractionOptions { get; set; }
}

[Table("factions")]
public sealed class Faction
{
    [Key]
    [Column("row_id")]
    public int RowId { get; set; }

    [Column("faction_name")]
    public required string FactionName { get; set; }

    [Column("description")]
    public required string Description { get; set; }

    [Column("leader")]
    public string? Leader { get; set; }

    [Column("relations_text")]
    public string? RelationsText { get; set; }

    [Column("headquarters")]
    public string? Headquarters { get; set; }
}

[Table("protagonist_info")]
public sealed class ProtagonistInfo
{
    [Key]
    [Column("row_id")]
    public int RowId { get; set; }

    [Column("name")]
    public required string Name { get; set; }

    [Column("gender")]
    public required string Gender { get; set; }

    [Column("age")]
    public int Age { get; set; }

    [Column("appearance")]
    public required string Appearance { get; set; }

    [Column("identity_text")]
    public required string IdentityText { get; set; }

    [Column("self_status")]
    public required string SelfStatus { get; set; }

    [Column("location_name")]
    public required string LocationName { get; set; }

    [Column("base_attributes")]
    public required string BaseAttributes { get; set; }

    [Column("special_attributes")]
    public string? SpecialAttributes { get; set; }

    [Column("resources_text")]
    public string? ResourcesText { get; set; }
}

[Table("important_npc")]
public sealed class ImportantNpc
{
    [Key]
    [Column("row_id")]
    public int RowId { get; set; }

    [Column("name")]
    public required string Name { get; set; }

    [Column("gender")]
    public required string Gender { get; set; }

    [Column("age")]
    public int Age { get; set; }

    [Column("brief_intro")]
    public required string BriefIntro { get; set; }

    [Column("appearance")]
    public required string Appearance { get; set; }

    [Column("identity_text")]
    public required string IdentityText { get; set; }

    [Column("base_attributes")]
    public required string BaseAttributes { get; set; }

    [Column("special_attributes")]
    public string? SpecialAttributes { get; set; }

    [Column("location_name")]
    public required string LocationName { get; set; }

    [Column("relations_text")]
    public string? RelationsText { get; set; }

    [Column("interaction_options")]
    public string? InteractionOptions { get; set; }

    [Column("past_experience")]
    public required string PastExperience { get; set; }

    [Column("self_status")]
    public string SelfStatus { get; set; } = "正常";
}

[Table("inventory")]
public sealed class InventoryItem
{
    [Key]
    [Column("row_id")]
    public int RowId { get; set; }

    [Column("item_name")]
    public required string ItemName { get; set; }

    [Column("item_type")]
    public required string ItemType { get; set; }

    [Column("quantity")]
    public int Quantity { get; set; } = 1;

    [Column("quality")]
    public required string Quality { get; set; }

    [Column("description")]
    public required string Description { get; set; }
}

[Table("equipment")]
public sealed class EquipmentItem
{
    [Key]
    [Column("row_id")]
    public int RowId { get; set; }

    [Column("equipment_name")]
    public required string EquipmentName { get; set; }

    [Column("equipment_type")]
    public required string EquipmentType { get; set; }

    [Column("quality")]
    public required string Quality { get; set; }

    [Column("status_text")]
    public required string StatusText { get; set; }

    [Column("description")]
    public required string Description { get; set; }
}

[Table("quests")]
public sealed class Quest
{
    [Key]
    [Column("row_id")]
    public int RowId { get; set; }

    [Column("quest_name")]
    public required string QuestName { get; set; }

    [Column("quest_type")]
    public required string QuestType { get; set; }

    [Column("priority_level")]
    public required string PriorityLevel { get; set; }

    [Column("target_desc")]
    public required string TargetDesc { get; set; }

    [Column("progress_text")]
    public required string ProgressText { get; set; }

    [Column("status_tag")]
    public required string StatusTag { get; set; }

    [Column("source_text")]
    public string? SourceText { get; set; }

    [Column("reward_text")]
    public string? RewardText { get; set; }
}

[Table("chronicle")]
public sealed class ChronicleEntry
{
    [Key]
    [Column("row_id")]
    public int RowId { get; set; }

    [Column("code_index")]
    public required string CodeIndex { get; set; }

    [Column("time_span")]
    public required string TimeSpan { get; set; }

    [Column("summary")]
    public required string Summary { get; set; }

    [Column("chronicle_text")]
    public required string ChronicleText { get; set; }
}

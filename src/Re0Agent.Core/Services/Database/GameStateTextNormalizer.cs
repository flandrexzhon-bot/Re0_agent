using Re0Agent.Core.Entities;

namespace Re0Agent.Core.Services.Database;

internal static class GameStateTextNormalizer
{
    public const int WorldMapEnvironmentDescMaxLength = 60;
    public const int MapElementDescMaxLength = 40;
    public const int FactionDescriptionMaxLength = 60;
    public const int ProtagonistAppearanceMaxLength = 60;
    public const int ProtagonistIdentityTextMaxLength = 40;
    public const int ImportantNpcBriefIntroMaxLength = 30;
    public const int ImportantNpcAppearanceMaxLength = 60;
    public const int ImportantNpcIdentityTextMaxLength = 40;
    public const int ImportantNpcPastExperienceMaxLength = 600;
    public const int InventoryItemNameMaxLength = 10;
    public const int InventoryDescriptionMaxLength = 60;
    public const int EquipmentDescriptionMaxLength = 40;
    public const int QuestTargetDescMaxLength = 100;
    public const int ChronicleSummaryMaxLength = 30;
    public const int ChronicleTextMaxLength = 2000;
    public const int CharacterMemoryTextMaxLength = 400;

    private static readonly Dictionary<string, Dictionary<string, int>> ColumnMaxLengths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["world_map_points"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["environment_desc"] = WorldMapEnvironmentDescMaxLength
        },
        ["map_elements"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["element_desc"] = MapElementDescMaxLength
        },
        ["factions"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["description"] = FactionDescriptionMaxLength
        },
        ["protagonist_info"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["appearance"] = ProtagonistAppearanceMaxLength,
            ["identity_text"] = ProtagonistIdentityTextMaxLength
        },
        ["important_npc"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["brief_intro"] = ImportantNpcBriefIntroMaxLength,
            ["appearance"] = ImportantNpcAppearanceMaxLength,
            ["identity_text"] = ImportantNpcIdentityTextMaxLength,
            ["past_experience"] = ImportantNpcPastExperienceMaxLength
        },
        ["inventory"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["item_name"] = InventoryItemNameMaxLength,
            ["description"] = InventoryDescriptionMaxLength
        },
        ["equipment"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["description"] = EquipmentDescriptionMaxLength
        },
        ["quests"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["target_desc"] = QuestTargetDescMaxLength
        },
        ["chronicle"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["summary"] = ChronicleSummaryMaxLength,
            ["chronicle_text"] = ChronicleTextMaxLength
        },
        ["character_memory"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["memory_text"] = CharacterMemoryTextMaxLength
        }
    };

    public static bool TryGetMaxLength(string tableName, string columnName, out int maxLength)
    {
        if (ColumnMaxLengths.TryGetValue(tableName, out var columns)
            && columns.TryGetValue(columnName, out maxLength))
        {
            return true;
        }

        maxLength = 0;
        return false;
    }

    public static ProtagonistInfo NormalizeProtagonist(
        ProtagonistInfo source,
        string defaultLocationName = "")
    {
        return new ProtagonistInfo
        {
            RowId = source.RowId,
            CharId = source.CharId,
            Name = NormalizeRequired(source.Name),
            Gender = NormalizeRequired(source.Gender),
            Age = source.Age,
            Appearance = LimitRequired(source.Appearance, ProtagonistAppearanceMaxLength),
            IdentityText = LimitRequired(source.IdentityText, ProtagonistIdentityTextMaxLength),
            SelfStatus = NormalizeRequired(source.SelfStatus, "正常"),
            LocationName = NormalizeRequired(source.LocationName, defaultLocationName),
            BaseAttributes = NormalizeRequired(source.BaseAttributes),
            SpecialAttributes = NormalizeOptional(source.SpecialAttributes),
            ResourcesText = NormalizeOptional(source.ResourcesText),
            Hp = source.Hp,
            MaxHp = source.MaxHp,
            Mp = source.Mp,
            MaxMp = source.MaxMp,
            Stamina = source.Stamina,
            MaxStamina = source.MaxStamina,
            Armor = source.Armor,
            SkillsJson = NormalizeOptional(source.SkillsJson)
        };
    }

    public static string LimitRequired(string? value, int maxLength, string fallback = "")
    {
        return LimitText(NormalizeRequired(value, fallback), maxLength);
    }

    public static string LimitText(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string NormalizeRequired(string? value, string fallback = "")
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? fallback : normalized;
    }

    private static string? NormalizeOptional(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}

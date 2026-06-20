using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Re0Agent.Core.Models;

/// <summary>
/// 角色技能。来自世界书 &lt;角色属性&gt; 的「技能列表」，随剧情可增删或置不可用。
/// JSON 字段名与世界书保持一致（中文键），便于直接映射。
/// </summary>
public sealed record CharacterSkill
{
    [JsonPropertyName("技能名")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("manaCost")]
    public int ManaCost { get; init; }

    [JsonPropertyName("staminaCost")]
    public int StaminaCost { get; init; }

    [JsonPropertyName("说明")]
    public string Description { get; init; } = string.Empty;

    /// <summary>是否当前可用（失去魔力、受伤、断契约等会被置 false）。</summary>
    [JsonPropertyName("可用")]
    public bool Available { get; init; } = true;
}

/// <summary>
/// <see cref="CharacterSkill"/> 列表与 skills_json 字符串之间的读写工具。
/// 容错解析：空串/无效 JSON 返回空列表，绝不抛异常打断回合。
/// </summary>
public static class SkillsSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

    public static IReadOnlyList<CharacterSkill> Parse(string? skillsJson)
    {
        if (string.IsNullOrWhiteSpace(skillsJson))
        {
            return [];
        }

        try
        {
            var skills = JsonSerializer.Deserialize<List<CharacterSkill>>(skillsJson, Options);
            return skills?.Where(s => !string.IsNullOrWhiteSpace(s.Name)).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Serialize(IEnumerable<CharacterSkill> skills)
    {
        return JsonSerializer.Serialize(skills, Options);
    }
}

namespace Re0Agent.Core.Services.Settings;

public sealed class RagQuery
{
    public required string Text { get; init; }
    public int Chapter { get; init; } = 1;
    public int MaxNonConstantEntries { get; init; } = 8;
    public int MaxCharacters { get; init; } = 14_000;
    public bool IncludeChapterEntries { get; init; } = true;

    /// <summary>
    /// 允许的分类键白名单（null = 不限制）。
    /// 格式: "world_settings" / "characters:爱蜜莉雅" / "locations:王都" / "plots" / "output_prompts"
    /// </summary>
    public IReadOnlyCollection<string>? AllowedCategories { get; init; }

    /// <summary>
    /// 这些分类键下的条目无需关键字命中即强制注入（仍受 MaxCharacters 预算约束）。
    /// 用于角色调度Agent需要纵览全部人物条目的场景。
    /// </summary>
    public IReadOnlyCollection<string>? ForceIncludeCategories { get; init; }
}

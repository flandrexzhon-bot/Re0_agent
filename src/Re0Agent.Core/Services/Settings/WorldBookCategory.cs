namespace Re0Agent.Core.Services.Settings;

/// <summary>
/// 世界书条目的分类键，格式为 category 或 category:subject。
/// 固定类别: world_settings / locations / characters / plots / output_prompts
/// </summary>
public static class WorldBookCategory
{
    public static string GetKey(WorldBookEntry entry)
    {
        var c = entry.Comment ?? "";

        if (IsOutputPrompt(c)) return "output_prompts";
        if (IsPlot(c)) return "plots";

        var subject = ExtractSubject(c);
        if (IsCharacter(c)) return subject is null ? "characters" : $"characters:{subject}";
        if (IsLocation(c)) return subject is null ? "locations" : $"locations:{subject}";
        return entry.Constant ? "world_settings" : "misc";
    }

    private static bool IsOutputPrompt(string c) =>
        c == "变量提示词" || c == "🔆状态栏🔆" ||
        c == "🔆章节设定(自动切章节)" || c == "[initvar]" ||
        c.StartsWith("⚙️全局要求", StringComparison.Ordinal);

    private static bool IsPlot(string c) =>
        c.StartsWith("第", StringComparison.Ordinal) && c.Contains('章');

    private static bool IsCharacter(string c) =>
        c.Contains("·人物:", StringComparison.Ordinal) || c.Contains("·角色:", StringComparison.Ordinal) ||
        c.Contains("人物:", StringComparison.Ordinal) || c.Contains("角色:", StringComparison.Ordinal) ||
        c.Contains("人物：", StringComparison.Ordinal) || c.Contains("角色：", StringComparison.Ordinal);

    private static bool IsLocation(string c) =>
        c.Contains("·地点:", StringComparison.Ordinal) || c.Contains("·城市:", StringComparison.Ordinal) ||
        c.Contains("地点:", StringComparison.Ordinal) || c.Contains("城市:", StringComparison.Ordinal) ||
        c.Contains("地点：", StringComparison.Ordinal) || c.Contains("城市：", StringComparison.Ordinal) ||
        c.Contains("大地图", StringComparison.Ordinal) || c.Contains("区域", StringComparison.Ordinal) ||
        c is "⚔️神圣弗拉基亚帝国" or "⚖️卡拉拉基都市国家" or "🐉露格尼卡亲龙王国" or "🙏古斯提科圣王国";

    /// <summary>提取冒号后的主体名（用于构造 category:subject 键）。</summary>
    private static string? ExtractSubject(string c)
    {
        var idx = Math.Max(c.LastIndexOf(':'), c.LastIndexOf('：'));
        return idx < 0 ? null : c[(idx + 1)..].Trim().NullIfEmpty();
    }

    private static string? NullIfEmpty(this string s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}

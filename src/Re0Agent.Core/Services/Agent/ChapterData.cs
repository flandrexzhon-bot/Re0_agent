using System.Text.RegularExpressions;
using Re0Agent.Core.Services.Settings;

namespace Re0Agent.Core.Services.Agent;

/// <summary>
/// 从世界书（BlackTeaWorldBook）的章节剧情条目中提取章节数据，供章节切换 Agent 引用。
/// 直接提供世界书原文，不截断、不摘要。
/// </summary>
public static class ChapterData
{
    private static readonly Regex ChapterNumberRegex = new(@"^第(\d+)章", RegexOptions.Compiled);

    /// <summary>返回指定章节号对应的世界书剧情条目（已按章节渲染模板），找不到返回 null。</summary>
    public static string? GetChapterPlot(
        IReadOnlyList<WorldBookEntry> entries,
        ChapterVariantRenderer renderer,
        int chapterNumber)
    {
        var entry = FindChapterEntry(entries, chapterNumber);
        if (entry is null) return null;
        var title = ExtractTitle(entry.Comment);
        var content = renderer.Render(entry.Content, chapterNumber).Trim();
        return $"第{chapterNumber}章「{title}」：\n{content}";
    }

    /// <summary>
    /// 返回从 currentChapter 往后 count 章的世界书原文，供帕秋莉判断是否切章。
    /// 每章提供完整剧情数据，不截断。
    /// </summary>
    public static string GetUpcomingText(
        IReadOnlyList<WorldBookEntry> entries,
        ChapterVariantRenderer renderer,
        int currentChapter,
        int count = 10)
    {
        var blocks = new List<string>();
        for (var n = currentChapter + 1; blocks.Count < count && n <= currentChapter + count * 4; n++)
        {
            var plot = GetChapterPlot(entries, renderer, n);
            if (plot is null) continue;
            blocks.Add(plot);
        }

        return blocks.Count == 0 ? "（已无后续章节）" : string.Join("\n\n---\n\n", blocks);
    }

    /// <summary>返回当前章节的世界书原文（当 GetChapterPlot 找不到时的兜底）。</summary>
    public static string GetCurrentChapterText(
        IReadOnlyList<WorldBookEntry> entries,
        ChapterVariantRenderer renderer,
        int chapterNumber)
    {
        return GetChapterPlot(entries, renderer, chapterNumber)
            ?? $"第{chapterNumber}章（世界书无预设剧情数据）";
    }

    private static WorldBookEntry? FindChapterEntry(IReadOnlyList<WorldBookEntry> entries, int chapterNumber)
    {
        return entries.FirstOrDefault(e =>
            e.Enabled
            && WorldBookCategory.GetKey(e) == "plots"
            && ParseChapterNumber(e.Comment) == chapterNumber);
    }

    private static int? ParseChapterNumber(string? comment)
    {
        if (string.IsNullOrEmpty(comment)) return null;
        var m = ChapterNumberRegex.Match(comment);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    private static string ExtractTitle(string? comment)
    {
        if (string.IsNullOrEmpty(comment)) return "无标题";
        var m = Regex.Match(comment, @"『(?<t>[^』]+)』");
        return m.Success ? m.Groups["t"].Value : comment;
    }
}

using System.Text.RegularExpressions;
using Re0Agent.Core.Services.Settings;

namespace Re0Agent.Core.Services.Agent;

/// <summary>
/// 从世界书（BlackTeaWorldBook）的章节剧情条目中提取章节数据，供章节切换 Agent 引用。
/// 章节内容以世界书 plots 条目为准，而非数据库摘要。
/// </summary>
public static class ChapterData
{
    private static readonly Regex ChapterNumberRegex = new(@"^第(\d+)章", RegexOptions.Compiled);
    private static readonly Regex SummarySectionRegex = new(
        @"#\s*章节总结\s*(?<body>[\s\S]*?)(?=\n\s*#\s|</章节剧情>|$)",
        RegexOptions.Compiled);

    /// <summary>返回指定章节号对应的世界书剧情条目（已按章节渲染模板）。找不到返回 null。</summary>
    public static string? GetChapterPlot(
        IReadOnlyList<WorldBookEntry> entries,
        ChapterVariantRenderer renderer,
        int chapterNumber)
    {
        var entry = FindChapterEntry(entries, chapterNumber);
        if (entry is null) return null;
        return renderer.Render(entry.Content, chapterNumber).Trim();
    }

    /// <summary>
    /// 返回从 currentChapter 往后 count 章的简要数据（标题 + 章节总结），供帕秋莉判断是否切章。
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
            var entry = FindChapterEntry(entries, n);
            if (entry is null) continue;

            var title = ExtractTitle(entry.Comment, n);
            var rendered = renderer.Render(entry.Content, n);
            var summary = ExtractSummary(rendered);
            blocks.Add($"第{n}章「{title}」：{summary}");
        }

        return blocks.Count == 0 ? "（已无后续章节）" : string.Join('\n', blocks);
    }

    /// <summary>返回当前章节的标题+总结摘要（当 GetChapterPlot 找不到完整条目时的兜底）。</summary>
    public static string GetCurrentChapterText(
        IReadOnlyList<WorldBookEntry> entries,
        ChapterVariantRenderer renderer,
        int chapterNumber)
    {
        var entry = FindChapterEntry(entries, chapterNumber);
        if (entry is null) return $"第{chapterNumber}章（世界书无预设剧情数据）";

        var title = ExtractTitle(entry.Comment, chapterNumber);
        var summary = ExtractSummary(renderer.Render(entry.Content, chapterNumber));
        return $"第{chapterNumber}章「{title}」：{summary}";
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

    private static string ExtractTitle(string? comment, int chapterNumber)
    {
        if (string.IsNullOrEmpty(comment)) return $"第{chapterNumber}章";
        var m = Regex.Match(comment, @"『(?<t>[^』]+)』");
        return m.Success ? m.Groups["t"].Value : comment;
    }

    private static string ExtractSummary(string renderedContent)
    {
        var m = SummarySectionRegex.Match(renderedContent);
        var body = m.Success ? m.Groups["body"].Value : renderedContent;
        var text = Regex.Replace(body, @"[-#\s]+", " ").Trim();
        return text.Length <= 160 ? text : text[..160] + "…";
    }
}
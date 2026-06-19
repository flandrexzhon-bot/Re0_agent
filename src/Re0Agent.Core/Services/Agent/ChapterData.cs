namespace Re0Agent.Core.Services.Agent;

public sealed record ChapterInfo(int Number, string Title, string Desc);

/// <summary>
/// Re:Zero 章节数据，供各 Agent 引用当前及后续章节剧情。
/// </summary>
public static class ChapterData
{
    public static IReadOnlyList<ChapterInfo> All { get; } = new List<ChapterInfo>
    {
        new(1, "开始的结束 (王都的一日)", "露格尼卡王国的王都，一切命运的起点，与银发半精灵美少女的邂逅与轮回。"),
        new(7, "自觉的感情 (宅邸的一周)", "罗兹瓦尔宅邸，平静日常生活下的暗流，诅咒、魔兽与接踵而至的绝望循环。"),
        new(18, "再访王都", "王选之局开启，与爱蜜莉雅产生裂痕，面对白鲸与魔女教的疯狂袭击。"),
        new(53, "千辛万苦抵达的地方 (圣域的试炼与强欲魔女)", "神秘的圣域与古老墓地，魔女的茶会，多重地狱般的因果纠缠与誓言的抉择。"),
        new(82, "开头总由来访者开始 (水门都市的抗战)", "受邀造访水门都市普利斯特拉，数个大罪司教同时突袭，前所未有的都市防卫战打响。")
    };

    public static ChapterInfo? Get(int chapterNumber) =>
        All.FirstOrDefault(c => c.Number <= chapterNumber) is { } match
            ? All.LastOrDefault(c => c.Number <= chapterNumber)
            : null;

    /// <summary>
    /// 返回从 currentChapter 开始的未来 N 章数据文本。
    /// </summary>
    public static string GetUpcomingText(int currentChapter, int count = 10)
    {
        var upcoming = All.Where(c => c.Number > currentChapter).Take(count).ToList();
        if (upcoming.Count == 0) return "（已无后续章节）";
        return string.Join('\n', upcoming.Select(c =>
            $"第{c.Number}章「{c.Title}」：{c.Desc}"));
    }

    public static string GetCurrentChapterText(int chapterNumber)
    {
        var chapter = Get(chapterNumber);
        return chapter is null
            ? $"第{chapterNumber}章（自定义章节，无预设数据）"
            : $"第{chapter.Number}章「{chapter.Title}」：{chapter.Desc}";
    }
}

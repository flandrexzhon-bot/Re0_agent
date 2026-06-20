using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;

namespace Re0Agent.Core.Services.Agent;

/// <summary>
/// 编年史(AM)选取策略：最近 5 条（必选）+ 关键词匹配最多的 15 条
/// （从剩余条目里按关键词命中数排序取前 15，已排除最近 5 条与序章 AM0000）。
/// 返回结果按时间（RowId）升序排列，供历史上下文使用。
/// </summary>
public static class ChronicleSelector
{
    private const int RecentCount = 5;
    private const int KeywordCount = 15;

    public static async Task<IReadOnlyList<ChronicleEntry>> SelectAsync(
        Re0AgentDbContext dbContext,
        IEnumerable<string> keywords,
        CancellationToken cancellationToken = default)
    {
        return await SelectAsync(dbContext, keywords, fallbackToPrologueWhenEmpty: false, cancellationToken);
    }

    /// <param name="fallbackToPrologueWhenEmpty">
    /// 当不存在任何正式编年史(AM0001+)时，是否回退为序章 AM0000。
    /// 用于角色调度等开局场景：此时唯一的历史就是序章。
    /// </param>
    public static async Task<IReadOnlyList<ChronicleEntry>> SelectAsync(
        Re0AgentDbContext dbContext,
        IEnumerable<string> keywords,
        bool fallbackToPrologueWhenEmpty,
        CancellationToken cancellationToken = default)
    {
        // 全部正式编年史（排除序章 AM0000），按时间升序。
        var all = await dbContext.Chronicle.AsNoTracking()
            .Where(c => c.CodeIndex != "AM0000")
            .OrderBy(c => c.RowId)
            .ToListAsync(cancellationToken);

        if (all.Count == 0 && fallbackToPrologueWhenEmpty)
        {
            // 仅有序章时，把 AM0000 当作历史返回。
            var prologue = await dbContext.Chronicle.AsNoTracking()
                .Where(c => c.CodeIndex == "AM0000")
                .OrderBy(c => c.RowId)
                .ToListAsync(cancellationToken);
            return prologue;
        }

        return Select(all, keywords);
    }

    /// <summary>纯函数版本，便于单测。输入需按 RowId 升序。</summary>
    public static IReadOnlyList<ChronicleEntry> Select(
        IReadOnlyList<ChronicleEntry> allAscending,
        IEnumerable<string> keywords)
    {
        if (allAscending.Count <= RecentCount)
        {
            return allAscending;
        }

        // 最近 5 条（末尾 5 条）必选。
        var recent = allAscending.Skip(allAscending.Count - RecentCount).ToList();
        var recentSet = recent.Select(c => c.RowId).ToHashSet();

        var terms = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Where(k => k.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 剩余条目按关键词命中数排序，取前 15（命中数为 0 的不入选）。
        var rest = allAscending.Where(c => !recentSet.Contains(c.RowId));
        var keywordPicked = terms.Count == 0
            ? []
            : rest
                .Select(c => (Entry: c, Score: CountMatches(c.ChronicleText, terms)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Entry.RowId)
                .Take(KeywordCount)
                .Select(x => x.Entry)
                .ToList();

        // 合并去重，按时间(RowId)升序输出。
        return keywordPicked.Concat(recent)
            .GroupBy(c => c.RowId)
            .Select(g => g.First())
            .OrderBy(c => c.RowId)
            .ToList();
    }

    private static int CountMatches(string text, IReadOnlyList<string> terms)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var score = 0;
        foreach (var term in terms)
        {
            var index = 0;
            while ((index = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                score++;
                index += term.Length;
            }
        }

        return score;
    }
}

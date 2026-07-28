using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;

namespace Re0Agent.Core.Services.Settings;

public static partial class LorebookConditionImporter
{
    public static async Task ImportAsync(
        Re0AgentDbContext dbContext,
        CancellationToken cancellationToken = default)
    {
        if (await dbContext.LorebookConditionEntries.AnyAsync(cancellationToken))
        {
            return;
        }

        var createdAt = DateTimeOffset.UtcNow.ToString("O");
        var entries = new List<LorebookConditionEntry>();
        foreach (var source in BlackTeaWorldBook.Entries)
        {
            var sourceKey = source.Keys.FirstOrDefault() ?? source.Comment;
            var order = 0;
            foreach (var (condition, content) in ExtractConditionalSegments(source.Content))
            {
                entries.Add(new LorebookConditionEntry
                {
                    SourceKey = sourceKey,
                    SourceOrder = order++,
                    LegacyCondition = condition,
                    FactPredicate = ConvertChapterCondition(condition),
                    Content = content,
                    CreatedAt = createdAt
                });
            }
        }

        if (entries.Count > 0)
        {
            dbContext.LorebookConditionEntries.AddRange(entries);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static string ConvertChapterCondition(string condition)
    {
        var match = ChapterConditionRegex().Match(condition);
        return match.Success
            ? $"legacy.chapter {match.Groups["operator"].Value} {match.Groups["value"].Value}"
            : $"legacy.template_condition:{condition}";
    }

    private static IEnumerable<(string Condition, string Content)> ExtractConditionalSegments(string content)
    {
        var frames = new Stack<ConditionalFrame>();
        var cursor = 0;
        foreach (Match match in TemplateTagRegex().Matches(content))
        {
            if (frames.Count > 0)
            {
                var segment = content[cursor..match.Index].Trim();
                if (!string.IsNullOrWhiteSpace(segment))
                {
                    yield return (string.Join(" && ", frames.Reverse().Select(frame => frame.CurrentCondition)), segment);
                }
            }

            var tag = match.Value[2..^2].Trim().Trim('_').Trim();
            var ifMatch = IfTagRegex().Match(tag);
            var elseIfMatch = ElseIfTagRegex().Match(tag);
            if (ifMatch.Success)
            {
                frames.Push(new ConditionalFrame(ifMatch.Groups["condition"].Value.Trim()));
            }
            else if (elseIfMatch.Success && frames.TryPop(out var frame))
            {
                frame.CurrentCondition = $"else if ({elseIfMatch.Groups["condition"].Value.Trim()})";
                frames.Push(frame);
            }
            else if (ElseTagRegex().IsMatch(tag) && frames.TryPop(out var elseFrame))
            {
                elseFrame.CurrentCondition = "else";
                frames.Push(elseFrame);
            }
            else if (EndTagRegex().IsMatch(tag) && frames.Count > 0)
            {
                frames.Pop();
            }
            cursor = match.Index + match.Length;
        }
    }

    private sealed class ConditionalFrame(string currentCondition)
    {
        public string CurrentCondition { get; set; } = currentCondition;
    }

    [GeneratedRegex("<%[\\s\\S]*?%>", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateTagRegex();

    [GeneratedRegex("^if\\s*\\((?<condition>[\\s\\S]*)\\)\\s*\\{$", RegexOptions.CultureInvariant)]
    private static partial Regex IfTagRegex();

    [GeneratedRegex("^}\\s*else\\s+if\\s*\\((?<condition>[\\s\\S]*)\\)\\s*\\{$", RegexOptions.CultureInvariant)]
    private static partial Regex ElseIfTagRegex();

    [GeneratedRegex("^}\\s*else\\s*\\{$", RegexOptions.CultureInvariant)]
    private static partial Regex ElseTagRegex();

    [GeneratedRegex("^}$", RegexOptions.CultureInvariant)]
    private static partial Regex EndTagRegex();

    [GeneratedRegex("getvar\\(\\s*['\"]stat_data\\.chapter['\"]\\s*\\)\\s*(?<operator><=|>=|==|<|>)\\s*(?<value>\\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ChapterConditionRegex();
}

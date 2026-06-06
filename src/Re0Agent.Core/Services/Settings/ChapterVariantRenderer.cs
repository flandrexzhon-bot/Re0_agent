using System.Text;
using System.Text.RegularExpressions;

namespace Re0Agent.Core.Services.Settings;

public sealed partial class ChapterVariantRenderer
{
    public string Render(string content, int chapter)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        var output = new StringBuilder(content.Length);
        var frames = new Stack<ConditionalFrame>();
        var cursor = 0;

        foreach (Match match in TemplateTagRegex().Matches(content))
        {
            if (IsActive(frames))
            {
                output.Append(content, cursor, match.Index - cursor);
            }

            HandleTag(match.Value, chapter, frames);
            cursor = match.Index + match.Length;
        }

        if (cursor < content.Length && IsActive(frames))
        {
            output.Append(content, cursor, content.Length - cursor);
        }

        return CleanupDanglingTemplateTags(output.ToString());
    }

    private static void HandleTag(string tag, int chapter, Stack<ConditionalFrame> frames)
    {
        var normalized = tag.Trim();

        var elseIfMatch = ElseIfRegex().Match(normalized);
        if (elseIfMatch.Success)
        {
            if (frames.Count == 0)
            {
                return;
            }

            var frame = frames.Pop();
            var condition = !frame.BranchMatched && EvaluateCondition(elseIfMatch.Groups["condition"].Value, chapter);
            frame.Active = frame.ParentActive && condition;
            frame.BranchMatched |= condition;
            frames.Push(frame);
            return;
        }

        if (ElseRegex().IsMatch(normalized))
        {
            if (frames.Count == 0)
            {
                return;
            }

            var frame = frames.Pop();
            var condition = !frame.BranchMatched;
            frame.Active = frame.ParentActive && condition;
            frame.BranchMatched = true;
            frames.Push(frame);
            return;
        }

        var ifMatch = IfRegex().Match(normalized);
        if (ifMatch.Success)
        {
            var parentActive = IsActive(frames);
            var condition = EvaluateCondition(ifMatch.Groups["condition"].Value, chapter);
            frames.Push(new ConditionalFrame(parentActive, condition, parentActive && condition));
            return;
        }

        if (EndIfRegex().IsMatch(normalized) && frames.Count > 0)
        {
            frames.Pop();
        }
    }

    private static bool EvaluateCondition(string condition, int chapter)
    {
        var match = ChapterConditionRegex().Match(condition);
        if (!match.Success)
        {
            return false;
        }

        var expected = int.Parse(match.Groups["value"].Value);
        return match.Groups["operator"].Value switch
        {
            "<" => chapter < expected,
            "<=" => chapter <= expected,
            ">" => chapter > expected,
            ">=" => chapter >= expected,
            "==" => chapter == expected,
            _ => false
        };
    }

    private static bool IsActive(IEnumerable<ConditionalFrame> frames)
    {
        return frames.All(frame => frame.Active);
    }

    private static string CleanupDanglingTemplateTags(string value)
    {
        return TemplateTagRegex().Replace(value, string.Empty);
    }

    [GeneratedRegex("<%[\\s\\S]*?%>", RegexOptions.Compiled)]
    private static partial Regex TemplateTagRegex();

    [GeneratedRegex("else\\s+if\\s*\\((?<condition>[\\s\\S]*)\\)\\s*\\{", RegexOptions.Compiled)]
    private static partial Regex ElseIfRegex();

    [GeneratedRegex("\\belse\\b", RegexOptions.Compiled)]
    private static partial Regex ElseRegex();

    [GeneratedRegex("(?<!else\\s)\\bif\\s*\\((?<condition>[\\s\\S]*)\\)\\s*\\{", RegexOptions.Compiled)]
    private static partial Regex IfRegex();

    [GeneratedRegex("^<%[_=\\-\\s]*\\}[_\\s]*%>$", RegexOptions.Compiled)]
    private static partial Regex EndIfRegex();

    [GeneratedRegex("getvar\\(\\s*['\"]stat_data\\.chapter['\"]\\s*\\)\\s*(?<operator><=|>=|==|<|>)\\s*(?<value>\\d+)", RegexOptions.Compiled)]
    private static partial Regex ChapterConditionRegex();

    private record struct ConditionalFrame(bool ParentActive, bool BranchMatched, bool Active)
    {
        public bool BranchMatched { get; set; } = BranchMatched;
        public bool Active { get; set; } = Active;
    }
}

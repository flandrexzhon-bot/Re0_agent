using System.Text.RegularExpressions;

namespace Re0Agent.Core.Services.Llm;

/// <summary>
/// 提示词宏展开。在发送给 LLM 之前对每条消息内容做一次处理，
/// 让同一条提示词每次输出有随机变化（仿 SillyTavern 宏）。
/// 支持两种格式：
///   {{random::选项1,选项2,...}}  — 标准 SillyTavern 格式（仅限非 C# 内插字符串，如数据库 SystemPrompt）
///   {#random::选项1,选项2,...#}  — C# 安全格式（可在 $$""" 内插原始字符串中使用）
/// 支持嵌套：内层宏会先于外层展开。
/// </summary>
public static partial class PromptMacros
{
    // {{random::选项1,选项2,...}} 或 {{random:选项1,选项2,...}}
    [GeneratedRegex(@"\{\{random::?(?<options>[^{}]*)\}\}", RegexOptions.Compiled)]
    private static partial Regex DoubleBraceRandom();

    // {#random::选项1,选项2,...#}  — C# 内插字符串安全格式
    [GeneratedRegex(@"\{#random::?(?<options>[^#}]*)#\}", RegexOptions.Compiled)]
    private static partial Regex HashBraceRandom();

    /// <summary>展开内容中的所有随机宏。null/空原样返回。</summary>
    public static string Expand(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content ?? string.Empty;
        }

        if (!content.Contains("{{random", StringComparison.OrdinalIgnoreCase)
            && !content.Contains("{#random", StringComparison.OrdinalIgnoreCase))
        {
            return content;
        }

        var text = content;
        // 反复展开以处理嵌套宏，设上限防止异常输入导致死循环。
        for (var pass = 0; pass < 10; pass++)
        {
            var changed = false;

            // 先处理 {#random::...#}（C# 安全格式），再处理 {{random::...}}（标准格式）。
            if (text.Contains("{#random", StringComparison.OrdinalIgnoreCase))
            {
                var next = HashBraceRandom().Replace(text, PickRandomOption);
                if (next != text)
                {
                    text = next;
                    changed = true;
                }
            }

            if (text.Contains("{{random", StringComparison.OrdinalIgnoreCase))
            {
                var next = DoubleBraceRandom().Replace(text, PickRandomOption);
                if (next != text)
                {
                    text = next;
                    changed = true;
                }
            }

            if (!changed) break;
        }

        return text;
    }

    private static string PickRandomOption(Match match)
    {
        var raw = match.Groups["options"].Value;
        var options = raw.Split(',')
            .Select(option => option.Trim())
            .Where(option => option.Length > 0)
            .ToArray();

        return options.Length == 0
            ? string.Empty
            : options[Random.Shared.Next(options.Length)];
    }
}

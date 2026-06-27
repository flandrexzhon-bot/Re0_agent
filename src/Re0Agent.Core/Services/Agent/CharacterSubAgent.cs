using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Llm;
using Re0Agent.Core.Services.Settings;

namespace Re0Agent.Core.Services.Agent;

public sealed class CharacterSubAgent(
    Re0AgentDbContext dbContext,
    AgentConfigResolver configResolver,
    PromptComposer promptComposer,
    ILlmClient llmClient,
    IRagService ragService,
    ChapterVariantRenderer chapterVariantRenderer)
{
    // 匹配 RAG 上下文中的 <设定条目 name="...">...</设定条目> 块。
    // 角色条目包含 "人物" 或 "角色" 字样，需被压缩为摘要。
    private static readonly Regex EntryBlockRegex = new(
        @"<设定条目\s+name=""(?<name>[^""]+)"">(?<body>[\s\S]*?)</设定条目>",
        RegexOptions.Compiled);

    // 从角色条目 body 中提取角色名（第一个 <名字> 或 # 前的第一行有效文本）。
    private static readonly Regex CharNameFromBodyRegex = new(
        @"^\s*<(?<name>[^>]+)>",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // 提取背景/种族描述的前几句实在内容。
    private static readonly Regex BackgroundLinesRegex = new(
        @"^-\s*(?<line>.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private const int MaxCharSummary = 160;

    public async Task<string> RunAsync(
        int chapter,
        IReadOnlyList<CharacterAgentProfile> allProfiles,
        CancellationToken cancellationToken = default)
    {
        var config = await configResolver.FindConfigAsync("CharacterSub", "CharacterSub", cancellationToken);

        var allEntries = await ragService.ListAllEntriesAsync(cancellationToken);

        // 当前地点术语（用于匹配 locations:X 类别）
        var locationTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var state = await dbContext.GlobalStates.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        AddTerm(locationTerms, state?.CurrentLocation);
        AddTerm(locationTerms, state?.CurrentMinorRegion);
        AddTerm(locationTerms, state?.CurrentMajorRegion);

        // 编年史(AM)：最近 5 条 + 关键词匹配最多的 15 条（关键词为出场角色名 + 当前地点）。
        // 序章 AM0000（前序故事）与其他 AM 地位相同，照常参与选取。
        var chronicleKeywords = allProfiles.Select(p => p.CharacterName).Concat(locationTerms);
        var recentChronicle = await ChronicleSelector.SelectAsync(
            dbContext, chronicleKeywords, cancellationToken);

        var history = recentChronicle.Count == 0
            ? "无历史记录。"
            : string.Join('\n', recentChronicle.Select(c => $"[{c.CodeIndex}] {c.ChronicleText}"));

        var allowedCategories = allEntries
            .Select(WorldBookCategory.GetKey)
            .Where(k => k == "world_settings"
                || k.StartsWith("characters:", StringComparison.Ordinal)
                || (k.StartsWith("locations:", StringComparison.Ordinal)
                    && locationTerms.Any(t => MatchesTerm(k["locations:".Length..], t))))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // 角色调度员需要纵览全部人物条目（无论是否在册、是否关键字命中），强制注入所有 characters:* 类别。
        var forceIncludeCategories = allowedCategories
            .Where(k => k.StartsWith("characters:", StringComparison.Ordinal))
            .ToList();

        var ragContext = await ragService.QueryAsync(
            new RagQuery
            {
                Text = string.Join(' ', allProfiles.Select(p => p.CharacterName)),
                Chapter = chapter,
                // 仿 SillyTavern 1M 上下文：角色调度员需纵览全部人物条目，给予极大预算确保不被截断。
                MaxCharacters = 1_000_000,
                AllowedCategories = allowedCategories,
                ForceIncludeCategories = forceIncludeCategories
            },
            cancellationToken);

        // 泉此方不需要角色完整世界书，给她 ≤160 字摘要即可。
        var compactContent = CompactCharacterEntries(ragContext.Content);
        var compactRag = new RagContext
        {
            Matches = ragContext.Matches,
            Content = compactContent
        };

        var currentLocation = state?.CurrentLocation ?? "";
        var chapterInfo = ChapterData.GetCurrentChapterText(allEntries, chapterVariantRenderer, chapter);

        var response = await llmClient.SendChatAsync(
            new LlmRequest
            {
                AgentName = "CharacterSub",
                Options = AgentConfigResolver.ToLlmOptions(config),
                Messages =
                [
                    LlmMessage.User(promptComposer.ComposeCharacterSub(allProfiles, currentLocation, compactRag, chapterInfo)),
                    LlmMessage.User(promptComposer.ComposeHistoryInjection(history)),
                    LlmMessage.User(promptComposer.ComposeCharacterSubThoughtGuide()),
                    LlmMessage.Assistant(PromptComposer.ThoughtPrefill)
                ]
            },
            cancellationToken);

        var match = Regex.Match(response.Content, @"<content>(.*?)</content>", RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : response.Content;
    }

    /// <summary>
    /// 将 RAG 上下文中的角色条目压缩为 ≤160 字摘要。
    /// 非角色条目（world_settings / locations）保持原样。
    /// </summary>
    internal static string CompactCharacterEntries(string? ragContent)
    {
        if (string.IsNullOrWhiteSpace(ragContent)) return ragContent ?? string.Empty;

        return EntryBlockRegex.Replace(ragContent, match =>
        {
            var name = match.Groups["name"].Value;
            var body = match.Groups["body"].Value;

            // 非角色条目原样保留
            if (!name.Contains("人物") && !name.Contains("角色"))
            {
                return match.Value;
            }

            var summary = BuildCharSummary(body);
            // 保留条目外壳但内容换为摘要
            return $"<设定条目 name=\"{name}\">\n{summary}\n</设定条目>\n";
        });
    }

    private static string BuildCharSummary(string body)
    {
        // 提取角色名
        var charName = "未知";
        var nameMatch = CharNameFromBodyRegex.Match(body);
        if (nameMatch.Success)
        {
            charName = nameMatch.Groups["name"].Value.Trim();
        }

        // 提取背景条目（以 "- " 开头的行）
        var bgLines = BackgroundLinesRegex.Matches(body)
            .Select(m => m.Groups["line"].Value.Trim())
            .Where(line => line.Length > 3)
            .ToList();

        var sb = new StringBuilder();
        sb.Append(charName);

        foreach (var line in bgLines)
        {
            var candidate = sb.ToString();
            var withLine = candidate + "；" + line;
            if (withLine.Length > MaxCharSummary)
            {
                // 剩余空间不足一行，尽力填满
                var remaining = MaxCharSummary - candidate.Length - 1;
                if (remaining > 5)
                {
                    sb.Append('；');
                    sb.Append(line[..Math.Min(remaining, line.Length)]);
                }
                break;
            }

            sb.Append('；');
            sb.Append(line);
        }

        return sb.ToString();
    }

    private static bool MatchesTerm(string subject, string term) =>
        subject.Contains(term, StringComparison.OrdinalIgnoreCase)
        || term.Contains(subject, StringComparison.OrdinalIgnoreCase);

    private static void AddTerm(HashSet<string> terms, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) terms.Add(value.Trim());
    }
}

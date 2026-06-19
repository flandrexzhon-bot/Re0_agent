using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Llm;
using Re0Agent.Core.Services.Settings;
using System.Text.RegularExpressions;

namespace Re0Agent.Core.Services.Agent;

public sealed class CharacterSubAgent(
    Re0AgentDbContext dbContext,
    AgentConfigResolver configResolver,
    PromptComposer promptComposer,
    ILlmClient llmClient,
    IRagService ragService,
    ChapterVariantRenderer chapterVariantRenderer)
{
    public async Task<string> RunAsync(
        int chapter,
        IReadOnlyList<CharacterAgentProfile> allProfiles,
        CancellationToken cancellationToken = default)
    {
        var config = await configResolver.FindConfigAsync("CharacterSub", "CharacterSub", cancellationToken);

        var recentChronicle = await dbContext.Chronicle
            .AsNoTracking()
            .OrderByDescending(c => c.RowId)
            .Take(5)
            .OrderBy(c => c.RowId)
            .ToListAsync(cancellationToken);

        var history = recentChronicle.Count == 0
            ? "无历史记录。"
            : string.Join('\n', recentChronicle.Select(c => $"[{c.CodeIndex}] {c.ChronicleText}"));

        var allEntries = await ragService.ListAllEntriesAsync(cancellationToken);

        // 当前地点术语（用于匹配 locations:X 类别）
        var locationTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var state = await dbContext.GlobalStates.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        AddTerm(locationTerms, state?.CurrentLocation);
        AddTerm(locationTerms, state?.CurrentMinorRegion);
        AddTerm(locationTerms, state?.CurrentMajorRegion);

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

        var currentLocation = state?.CurrentLocation ?? "";
        var chapterInfo = ChapterData.GetCurrentChapterText(allEntries, chapterVariantRenderer, chapter);

        var response = await llmClient.SendChatAsync(
            new LlmRequest
            {
                AgentName = "CharacterSub",
                Options = AgentConfigResolver.ToLlmOptions(config),
                Messages =
                [
                    LlmMessage.User(promptComposer.ComposeCharacterSub(allProfiles, history, currentLocation, ragContext, chapterInfo))
                ]
            },
            cancellationToken);

        var match = Regex.Match(response.Content, @"<content>(.*?)</content>", RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : response.Content;
    }

    private static bool MatchesTerm(string subject, string term) =>
        subject.Contains(term, StringComparison.OrdinalIgnoreCase)
        || term.Contains(subject, StringComparison.OrdinalIgnoreCase);

    private static void AddTerm(HashSet<string> terms, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) terms.Add(value.Trim());
    }
}

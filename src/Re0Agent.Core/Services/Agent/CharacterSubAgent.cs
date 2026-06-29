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
    public async Task<string> RunAsync(
        GameRound round,
        IReadOnlyList<CharacterAgentProfile> allProfiles,
        CancellationToken cancellationToken = default)
    {
        var chapter = round.Chapter;
        var gmOpening = round.GmOpening;
        var playerInput = round.PlayerInput;

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

        // 历史上下文与 GM/角色 Agent 一致：编年史(AM) + 最近若干大回合原版全文（round.PreviousRounds）。
        // 之前泉此方只注入编年史、漏了前序回合原版，导致「调度时看不到原本三回合上下文」。
        var history = RoundContextBuilder.BuildHistory(round, recentChronicle);

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

        // 泉此方与开普勒共享同一份现世处境上下文：完整世界书（不再压缩角色条目）+ 数据库摘要。
        var dbSummary = await DbSummaryBuilder.BuildAsync(dbContext, cancellationToken);

        var currentLocation = state?.CurrentLocation ?? "";
        var chapterInfo = ChapterData.GetCurrentChapterText(allEntries, chapterVariantRenderer, chapter);

        var response = await llmClient.SendChatAsync(
            new LlmRequest
            {
                AgentName = "CharacterSub",
                Options = AgentConfigResolver.ToLlmOptions(config),
                Messages =
                [
                    LlmMessage.User(promptComposer.ComposeCharacterSub(allProfiles, currentLocation, ragContext, chapterInfo, gmOpening, playerInput, dbSummary)),
                    LlmMessage.User(promptComposer.ComposeHistoryInjection(history)),
                    // 深度注入(depth=0)：GM 开场 + 主角玩家输入，紧贴生成点放在最高注意力位，
                    // 与原版上下文同等深度，确保泉此方调度 NPC 时始终盯着「本回合刚发生了什么」。
                    LlmMessage.User(promptComposer.ComposeCharacterSubFocusInjection(allProfiles, gmOpening, playerInput)),
                    LlmMessage.User(promptComposer.ComposeCharacterSubThoughtGuide()),
                    LlmMessage.Assistant(PromptComposer.ThoughtPrefill)
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

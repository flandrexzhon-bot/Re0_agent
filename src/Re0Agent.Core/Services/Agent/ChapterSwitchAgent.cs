using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Llm;
using Re0Agent.Core.Services.Settings;

namespace Re0Agent.Core.Services.Agent;

public sealed class ChapterSwitchAgent(
    Re0AgentDbContext dbContext,
    AgentConfigResolver configResolver,
    PromptComposer promptComposer,
    ILlmClient llmClient,
    IRagService ragService,
    ChapterVariantRenderer chapterVariantRenderer)
{
    /// <summary>
    /// 分析当前回合内容，判断是否需要切换章节。
    /// 若需要切换，返回更新后的章节号；若不变，返回 null。
    /// </summary>
    public async Task<int?> RunAsync(
        GameRound round,
        CancellationToken cancellationToken = default)
    {
        var config = await configResolver.FindConfigAsync("ChapterSwitch", "章节切换", cancellationToken);
        var chapter = round.Chapter;
        var state = await dbContext.GlobalStates.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

        var allEntries = await ragService.ListAllEntriesAsync(cancellationToken);

        // 章节剧情以世界书 plots 条目为准（已按章节渲染模板）。
        var currentChapterPlot = ChapterData.GetChapterPlot(allEntries, chapterVariantRenderer, chapter)
            ?? ChapterData.GetCurrentChapterText(allEntries, chapterVariantRenderer, chapter);
        var upcomingChapters = ChapterData.GetUpcomingText(allEntries, chapterVariantRenderer, chapter);

        // 历史上下文：本回合原版全文（GM开场 + 全部角色行动）+ 最近 5 条编年史(AM)总结。
        // 章节切换 Agent 与填表 Agent 并发运行，本回合内容此刻尚未写入 chronicle，
        // 因此必须直接用 round 的原始内容，而非只读 DB 的 AM 总结，否则会显示"无历史"。
        var recentChronicle = await dbContext.Chronicle.AsNoTracking()
            .Where(c => c.CodeIndex != "AM0000")
            .OrderByDescending(c => c.RowId)
            .Take(5)
            .OrderBy(c => c.RowId)
            .ToListAsync(cancellationToken);

        var history = RoundContextBuilder.BuildHistory(round, recentChronicle);

        var prologue = await dbContext.Chronicle.AsNoTracking()
            .Where(c => c.CodeIndex == "AM0000")
            .Select(c => c.ChronicleText)
            .FirstOrDefaultAsync(cancellationToken);

        var lastChronicle = recentChronicle.LastOrDefault()?.ChronicleText;

        var dbSummary = await BuildDbSummaryAsync(state, cancellationToken);

        // 设定背景：当前章节及当前地点相关的世界书条目。
        var ragContext = await ragService.QueryAsync(
            new RagQuery
            {
                Text = $"{state?.CurrentMajorRegion} {state?.CurrentMinorRegion} {state?.CurrentLocation}",
                Chapter = chapter,
                AllowedCategories = ["world_settings", "locations"]
            },
            cancellationToken);

        var response = await llmClient.SendChatAsync(
            new LlmRequest
            {
                AgentName = "章节切换Agent",
                Options = AgentConfigResolver.ToLlmOptions(config),
                Messages =
                [
                    LlmMessage.System(config?.SystemPrompt ?? "你是帕秋莉，负责判断章节是否需要切换。只输出 <update>_.set</update> 指令，不需要输出其他内容。"),
                    LlmMessage.User(promptComposer.ComposeChapterSwitch(
                        state, history, currentChapterPlot, upcomingChapters,
                        ragContext, prologue, dbSummary, lastChronicle)),
                    LlmMessage.Assistant(PromptComposer.ThoughtPrefill)
                ]
            },
            cancellationToken);

        return ParseChapterSwitch(response.Content, chapter);
    }

    private async Task<string> BuildDbSummaryAsync(GlobalState? state, CancellationToken cancellationToken)
    {
        var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var stateStr = state is null ? "无" :
            $"位置:{state.CurrentLocation}/{state.CurrentMinorRegion}/{state.CurrentMajorRegion}，时间:{state.CurTime}，章节:{state.CurrentChapter}";
        var protagonistStr = protagonist is null ? "无" :
            $"{protagonist.Name}，位于{protagonist.LocationName}，状态:{protagonist.SelfStatus}";
        return $"[全局状态] {stateStr}\n[主角] {protagonistStr}";
    }

    private static int? ParseChapterSwitch(string content, int currentChapter)
    {
        var match = Regex.Match(
            content,
            @"_\.set\(\s*['""]chapter['""]\s*,\s*(\d+)\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var newChapter))
        {
            return null;
        }

        if (newChapter == currentChapter)
        {
            return null;
        }

        return newChapter;
    }
}

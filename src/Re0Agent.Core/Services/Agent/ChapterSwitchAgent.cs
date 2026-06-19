using System.Text.RegularExpressions;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Llm;

namespace Re0Agent.Core.Services.Agent;

public sealed class ChapterSwitchAgent(
    AgentConfigResolver configResolver,
    PromptComposer promptComposer,
    ILlmClient llmClient)
{
    /// <summary>
    /// 分析当前回合内容，判断是否需要切换章节。
    /// 若需要切换，返回更新后的章节号；若不变，返回 null。
    /// </summary>
    public async Task<int?> RunAsync(
        GameRound round,
        string? dbSummary = null,
        string? lastChronicle = null,
        CancellationToken cancellationToken = default)
    {
        var config = await configResolver.FindConfigAsync("ChapterSwitch", "章节切换", cancellationToken);
        var chapter = round.Chapter;

        var chapterInfo = ChapterData.GetCurrentChapterText(chapter);
        var chapterFuture = ChapterData.GetUpcomingText(chapter);

        var response = await llmClient.SendChatAsync(
            new LlmRequest
            {
                AgentName = "章节切换Agent",
                Options = AgentConfigResolver.ToLlmOptions(config),
                Messages =
                [
                    LlmMessage.System(config?.SystemPrompt ?? "你是帕秋莉，负责判断章节是否需要切换。只输出 <update>_.set</update> 指令，不需要输出其他内容。"),
                    LlmMessage.User(promptComposer.ComposeChapterSwitch(chapterInfo, chapterFuture, dbSummary, lastChronicle))
                ]
            },
            cancellationToken);

        return ParseChapterSwitch(response.Content, chapter);
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

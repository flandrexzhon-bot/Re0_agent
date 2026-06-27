using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;

namespace Re0Agent.Core.Services.Agent;

/// <summary>
/// 构建供各 Agent 复用的「历史上下文」：当前大回合的原版全文
/// （GM 开场 + 全部角色行动）+ 最近若干条编年史(AM)总结。
/// 章节切换/填表等 Agent 与本回合并发运行，本回合内容此刻尚未写入 chronicle，
/// 必须直接取 round 的原始内容，否则会出现"明明有上下文却显示无历史"。
/// </summary>
public static class RoundContextBuilder
{
    /// <summary>当前回合原版全文 + 编年史总结，拼成完整历史上下文。</summary>
    public static string BuildHistory(
        GameRound round,
        IReadOnlyList<ChronicleEntry> recentChronicle)
    {
        var sections = new List<string>();

        if (recentChronicle.Count > 0)
        {
            var summaries = string.Join('\n', recentChronicle.Select(c => $"[{c.CodeIndex}] {c.ChronicleText}"));
            sections.Add($"【过往编年史总结(AM)】\n{summaries}");
        }

        foreach (var prev in round.PreviousRounds)
        {
            var transcript = BuildCurrentRoundTranscript(prev);
            if (!string.IsNullOrWhiteSpace(transcript))
                sections.Add($"【上回合原版上下文(编号 {prev.RoundIndex})】\n{transcript}");
        }

        var current = BuildCurrentRoundTranscript(round);
        if (!string.IsNullOrWhiteSpace(current))
        {
            sections.Add($"【本回合原版上下文(编号 {round.RoundIndex})】\n{current}");
        }

        return sections.Count == 0 ? "无历史记录。" : string.Join("\n\n", sections);
    }

    /// <summary>当前大回合的原版逐条记录：GM 开场 + 每个角色的行动/判定/结果。</summary>
    public static string BuildCurrentRoundTranscript(GameRound round)
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(round.GmOpening))
        {
            lines.Add($"GM开场：{round.GmOpening.Trim()}");
        }

        foreach (var turn in round.CharacterTurns)
        {
            if (turn.Skipped)
            {
                lines.Add($"{turn.OrderNumber}. {turn.CharacterName}：（跳过/旁观）");
                continue;
            }

            var action = string.IsNullOrWhiteSpace(turn.ActionText) ? "（无）" : turn.ActionText!.Trim();
            var dice = turn.DiceResult is null || string.IsNullOrWhiteSpace(turn.DiceResult.SuccessLevel)
                ? ""
                : $"（判定：{turn.DiceResult.SuccessLevel}）";
            lines.Add($"{turn.OrderNumber}. {turn.CharacterName}：{action}{dice}");
        }

        return string.Join('\n', lines);
    }
}

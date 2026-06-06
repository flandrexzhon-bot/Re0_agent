using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Settings;

namespace Re0Agent.Core.Services.Agent;

public sealed class PromptComposer
{
    public string ComposeGmOpening(
        GlobalState? globalState,
        IReadOnlyList<CharacterAgentProfile> profiles,
        RagContext? ragContext = null)
    {
        var stateText = globalState is null
            ? "当前数据库还没有global_state，使用Phase2默认开场。"
            : $"地点:{globalState.CurrentLocation}/{globalState.CurrentMinorRegion}/{globalState.CurrentMajorRegion}; 时间:{globalState.CurTime}; 章节:{globalState.CurrentChapter}";

        var characters = string.Join("，", profiles.Select(profile =>
            profile.IsPlayerControlled ? $"{profile.CharacterName}(主角/最后行动)" : profile.CharacterName));

        return $"""
        你是Re:Zero桌游式角色扮演系统的GM Agent。
        职责：介绍当前大回合情况，安排位号，并保持幕后总控。
        当前状态：{stateText}
        在场角色Agent：{characters}
        {FormatRagContext(ragContext)}
        输出要求：中文，给出简短开场和位号。主角固定最后行动。
        """;
    }

    public string ComposeGmJudgement(CharacterTurn turn, RagContext? ragContext = null)
    {
        return $"""
        你是GM Agent。请对以下角色回合行为做简短裁判。
        角色：{turn.CharacterName}
        行为：{turn.ActionText}
        {FormatRagContext(ragContext)}
        {DiceRulesText()}
        输出要求：必须包含一行“判定：<DSL>”。检定建议不入库，禁止提到check_suggestions表。
        若本次判定导致主角死亡，额外输出独立一行“死亡回归：<死因>”。
        """;
    }

    public string ComposeGmSummary(GameRound round, RagContext? ragContext = null)
    {
        var turns = string.Join('\n', round.CharacterTurns.Select(turn =>
            $"{turn.OrderNumber}. {turn.CharacterName}: {(turn.Skipped ? "跳过" : turn.ActionText)} / {turn.GmJudgement} / 骰子:{FormatDiceResult(turn.DiceResult)}"));

        return $"""
        你是GM Agent。请总结本大回合并轻微推进剧情。
        回合编号：{round.RoundIndex}
        {FormatRagContext(ragContext)}
        回合记录：
        {turns}
        输出要求：中文，客观概括，保留桌游回合感。
        """;
    }

    public string ComposeCharacterTurn(
        CharacterAgentProfile profile,
        GameRound round,
        IReadOnlyList<CharacterMemory> recentMemories,
        string? playerInstruction,
        RagContext? ragContext = null)
    {
        var memories = recentMemories.Count == 0
            ? "无近期私有记忆。"
            : string.Join('\n', recentMemories.Select(memory => $"- {memory.RoundIndex}: {memory.MemoryText} ({memory.EmotionalState})"));

        var previousTurns = round.CharacterTurns.Count == 0
            ? "本轮尚无其他角色行动。"
            : string.Join('\n', round.CharacterTurns.Select(turn => $"- {turn.CharacterName}: {turn.ActionText}"));

        var playerText = profile.IsPlayerControlled
            ? $"玩家输入：{playerInstruction ?? "玩家选择旁观/跳过。"}"
            : "这是NPC角色Agent，请自主选择符合角色的行动。";

        return $"""
        你是角色Agent，只扮演一个角色：{profile.CharacterName}。
        主角标记：{profile.IsPlayerControlled}
        当前状态引用：{profile.CurrentStateReference}
        角色基础设定引用：{profile.WorldBookEntryKey ?? "未指定"}
        {FormatRagContext(ragContext)}
        私有记忆：
        {memories}
        本轮上文：
        GM开场：{round.GmOpening}
        之前角色行动：
        {previousTurns}
        {playerText}
        输出要求：中文，只输出该角色的动作、对话、回应或不作为，不要替其他角色行动。
        """;
    }

    public string ComposeFormAgent(
        GameRound round,
        string databaseSummary)
    {
        var turns = string.Join('\n', round.CharacterTurns.Select(turn =>
            $"{turn.OrderNumber}. {turn.CharacterName}: skipped={turn.Skipped}; action={turn.ActionText}; judgement={turn.GmJudgement}; dice={FormatDiceResult(turn.DiceResult)}; response={turn.ResultResponse}"));

        return $$"""
        你是填表Agent。职责：把完整大回合记录转为SQLite SQL。
        只能输出JSON对象，格式：{"sql":["INSERT ...","UPDATE ..."]}
        只允许INSERT或UPDATE，不要输出DELETE、DDL、PRAGMA。
        应至少写入chronicle，并为本轮涉及角色写入character_memory。
        当前DB摘要：
        {{databaseSummary}}
        大回合：
        编号：{{round.RoundIndex}}
        GM开场：{{round.GmOpening}}
        角色回合：
        {{turns}}
        GM总结：{{round.GmSummary}}
        """;
    }

    private static string FormatRagContext(RagContext? ragContext)
    {
        if (ragContext is null || string.IsNullOrWhiteSpace(ragContext.Content))
        {
            return "设定上下文：本次未命中额外RAG设定。";
        }

        return $"""
        设定上下文：
        {ragContext.Content}
        """;
    }

    private static string DiceRulesText()
    {
        return """
        骰子DSL：
        - 无 / 必成 / 必败
        - 检定 <角色> <属性> [难度=普通|困难|极难] [奖惩=奖励1|惩罚1]
        - 对抗 <角色> <属性> vs <角色> <属性> [难度=普通|困难|极难] [奖惩=奖励1|惩罚1]
        - 魔法 <角色> <属性> [等级=基础|El|Ul|Al] [门=<属性名>]
        - 精灵术 <角色> <契约属性> [活跃=是|否]
        - 权能 <角色> <属性> [类型=死亡回归|Invisible Providence|Cor Leonis|狮子的心脏]
        - 瘴气 <当前等级>
        - 加护 <角色> <属性> [对抗权能=是|否]
        - 魔法对抗 <角色> <属性> vs <角色> <属性> [相克=是|否]
        只有关键且有悬念的行动才检定；属性名必须来自角色数据库的普通属性或特殊属性。
        """;
    }

    private static string FormatDiceResult(DiceResult? result)
    {
        if (result is null)
        {
            return "未执行";
        }

        var roll = result.Roll is null ? "" : $" roll={result.Roll}/{result.TargetAfterModifiers ?? result.Target}";
        var error = string.IsNullOrWhiteSpace(result.Error) ? "" : $" error={result.Error}";
        return $"{result.Command} => {result.Outcome}/{result.SuccessLevel}{roll}{error}";
    }
}

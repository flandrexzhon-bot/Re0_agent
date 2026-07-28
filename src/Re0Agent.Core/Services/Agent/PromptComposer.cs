using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Settings;

namespace Re0Agent.Core.Services.Agent;

public sealed class PromptComposer
{
    public const string ThoughtPrefill = "<thought>";

    public string ComposeCharacterAction(
        CharacterAgentProfile profile,
        TimelineEvent contextEvent,
        IReadOnlyList<CharacterMemory> memories,
        ActorMotivation motivation,
        RagContext? ragContext,
        string databaseSummary)
    {
        var memoryText = memories.Count == 0
            ? "无近期私有记忆。"
            : string.Join('\n', memories.Select(memory =>
                $"- {memory.WorldTime}：{memory.MemoryText}（{memory.EmotionalState ?? "平静"}）"));
        return $"""
            你只扮演 {profile.CharacterName}。只输出该角色自己的语言、动作和必要的主观感受，不能替其他角色发言、决定行动或切换场景。

            当前已提交事件：{contextEvent.EventType}
            事件内容：{contextEvent.Content}
            抽象行动动机：{motivation.AbstractMotivation}
            紧迫度：{motivation.Urgency}
            允许目标：{string.Join("、", motivation.AllowedGoals)}
            知识约束：{string.Join("；", motivation.KnowledgeConstraints)}
            你的状态引用：{profile.CurrentStateReference}
            你可见的世界状态：{databaseSummary}
            你的私有记忆：
            {memoryText}
            允许的背景资料：
            {ragContext?.Content ?? "无额外资料。"}

            不得叙述开普勒的环境描写、导演计划、章节规划或你无法得知的离屏事实。不要输出 Markdown、骰子命令或他人台词。
            """;
    }

    public string ComposeCharacterTurnThoughtGuide() =>
        "先依据自己的已知事实、关系和目标判断行动，再只输出角色本人的语言和动作。";

    public string ComposeStateChangeProposal(TimelineEvent eventRecord, string projectionSummary) =>
        $"事件类型：{eventRecord.EventType}\n事件内容：{eventRecord.Content}\n当前投影：{projectionSummary}";
}

using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Llm;
using Re0Agent.Core.Services.Settings;

namespace Re0Agent.Core.Services.Agent;

public sealed class GmAgent(
    Re0AgentDbContext dbContext,
    AgentConfigResolver configResolver,
    PromptComposer promptComposer,
    ILlmClient llmClient,
    IRagService ragService,
    CharacterNameResolver nameResolver)
{
    public async Task<string> CreateOpeningAsync(
        GameRound round,
        IReadOnlyList<CharacterAgentProfile> profiles,
        string slotList,
        CancellationToken cancellationToken = default)
    {
        var config = await configResolver.FindConfigAsync("GM", "GM", cancellationToken);
        var state = await dbContext.GlobalStates.FindAsync([1], cancellationToken);

        // Scope RAG: world_settings + plots + locations (keyword), force-include slot characters
        var aliasGroups = await nameResolver.LoadGroupsAsync(cancellationToken);
        var slotCharCategories = ParseNpcNamesFromSlots(slotList)
            .Select(n => CharacterNameResolver.ResolveCanonical(aliasGroups, n) ?? n)
            .Distinct()
            .Select(c => $"characters:{c}")
            .ToList();

        var ragContext = await ragService.QueryAsync(
            new RagQuery
            {
                Text = $"{state?.CurrentMajorRegion} {state?.CurrentMinorRegion} {state?.CurrentLocation} {round.PlayerInput}",
                Chapter = round.Chapter,
                AllowedCategories = ["world_settings", "locations", "plots", "output_prompts"],
                ForceIncludeCategories = slotCharCategories
            },
            cancellationToken);

        var prologue = await dbContext.Chronicle.AsNoTracking()
            .Where(c => c.CodeIndex == "AM0000")
            .Select(c => c.ChronicleText)
            .FirstOrDefaultAsync(cancellationToken);

        var lastChronicle = await dbContext.Chronicle.AsNoTracking()
            .Where(c => c.CodeIndex != "AM0000")
            .OrderByDescending(c => c.RowId)
            .Select(c => c.ChronicleText)
            .FirstOrDefaultAsync(cancellationToken);

        var dbSummary = await BuildDbSummaryAsync(cancellationToken);

        var response = await llmClient.SendChatAsync(
            new LlmRequest
            {
                AgentName = "GM",
                Options = AgentConfigResolver.ToLlmOptions(config),
                Messages =
                [
                    LlmMessage.System(config?.SystemPrompt ?? "你是Re:Zero桌游GM。"),
                    LlmMessage.User(promptComposer.ComposeGmOpening(state, profiles, slotList, ragContext, prologue, dbSummary, lastChronicle))
                ]
            },
            cancellationToken);

        return response.Content;
    }

    public async Task<string> JudgeTurnAsync(
        GameRound round,
        CharacterTurn turn,
        CancellationToken cancellationToken = default)
    {
        // 投骰GM: uses "Dice" routing (falls back to "GM"), sees only acting character + dice rules
        var config = await configResolver.FindConfigAsync("DiceGM", "GM", cancellationToken);

        var aliasGroups = await nameResolver.LoadGroupsAsync(cancellationToken);
        var canonical = CharacterNameResolver.ResolveCanonical(aliasGroups, turn.CharacterName) ?? turn.CharacterName;

        var ragContext = await ragService.QueryAsync(
            new RagQuery
            {
                Text = $"{turn.CharacterName} {turn.ActionText}",
                Chapter = round.Chapter,
                AllowedCategories = ["world_settings"],
                ForceIncludeCategories = [$"characters:{canonical}"]
            },
            cancellationToken);

        var charAttrs = await LoadCharacterAttrsAsync(turn.CharacterName, cancellationToken);

        var response = await llmClient.SendChatAsync(
            new LlmRequest
            {
                AgentName = "GM",
                Options = AgentConfigResolver.ToLlmOptions(config),
                Messages =
                [
                    LlmMessage.System(config?.SystemPrompt ?? "你是Re:Zero桌游GM。"),
                    LlmMessage.User(promptComposer.ComposeGmJudgement(turn, ragContext, charAttrs))
                ]
            },
            cancellationToken);

        return response.Content;
    }

    public async Task<string> SummarizeAsync(
        GameRound round,
        CancellationToken cancellationToken = default)
    {
        var config = await configResolver.FindConfigAsync("GM", "GM", cancellationToken);

        var aliasGroups = await nameResolver.LoadGroupsAsync(cancellationToken);
        var charCategories = round.CharacterTurns
            .Select(t => CharacterNameResolver.ResolveCanonical(aliasGroups, t.CharacterName) ?? t.CharacterName)
            .Distinct()
            .Select(c => $"characters:{c}")
            .ToList();

        var ragContext = await ragService.QueryAsync(
            new RagQuery
            {
                Text = $"{round.GmOpening} {string.Join(' ', round.CharacterTurns.Select(t => $"{t.CharacterName} {t.ActionText} {t.GmJudgement}"))}",
                Chapter = round.Chapter,
                AllowedCategories = ["world_settings", "locations", "plots"],
                ForceIncludeCategories = charCategories
            },
            cancellationToken);

        var response = await llmClient.SendChatAsync(
            new LlmRequest
            {
                AgentName = "GM",
                Options = AgentConfigResolver.ToLlmOptions(config),
                Messages =
                [
                    LlmMessage.System(config?.SystemPrompt ?? "你是Re:Zero桌游GM。"),
                    LlmMessage.User(promptComposer.ComposeGmSummary(round, ragContext))
                ]
            },
            cancellationToken);

        return response.Content;
    }

    private async Task<string> BuildDbSummaryAsync(CancellationToken cancellationToken)
    {
        var state = await dbContext.GlobalStates.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var npcs = await dbContext.ImportantNpcs.AsNoTracking()
            .Select(n => $"{n.Name}（{n.LocationName}，{n.SelfStatus}，{n.BaseAttributes}）")
            .ToListAsync(cancellationToken);

        var stateStr = state is null ? "无" :
            $"位置:{state.CurrentLocation}/{state.CurrentMinorRegion}/{state.CurrentMajorRegion}，时间:{state.CurTime}，章节:{state.CurrentChapter}";
        var protagonistStr = protagonist is null ? "无" :
            $"{protagonist.Name}，位于{protagonist.LocationName}，状态:{protagonist.SelfStatus}，{protagonist.BaseAttributes}{(string.IsNullOrWhiteSpace(protagonist.SpecialAttributes) ? "" : "，" + protagonist.SpecialAttributes)}";
        var npcStr = npcs.Count == 0 ? "（无）" : string.Join("；", npcs);

        return $"[全局状态] {stateStr}\n[主角] {protagonistStr}\n[在册NPC] {npcStr}";
    }

    private async Task<string> LoadCharacterAttrsAsync(string characterName, CancellationToken cancellationToken)
    {
        var npc = await dbContext.ImportantNpcs.AsNoTracking()
            .FirstOrDefaultAsync(n => n.Name == characterName, cancellationToken);
        if (npc is not null)
            return $"{npc.BaseAttributes}{(string.IsNullOrWhiteSpace(npc.SpecialAttributes) ? "" : "；" + npc.SpecialAttributes)}";

        var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (protagonist?.Name == characterName)
            return $"{protagonist.BaseAttributes}{(string.IsNullOrWhiteSpace(protagonist.SpecialAttributes) ? "" : "；" + protagonist.SpecialAttributes)}";

        return "（未找到角色属性数据）";
    }

    private static IEnumerable<string> ParseNpcNamesFromSlots(string? slotList)
    {
        if (string.IsNullOrWhiteSpace(slotList)) yield break;
        foreach (var line in slotList.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains("最后行动", StringComparison.Ordinal)) continue;
            var m = System.Text.RegularExpressions.Regex.Match(line, @"\d+\s*(?:号位|号|位|\.|：|:)\s*([^\r\n]+)");
            if (m.Success)
                yield return m.Groups[1].Value.Trim().Trim('*', '-', ' ', '。', '：', ':', '－', '[', ']');
        }
    }
}

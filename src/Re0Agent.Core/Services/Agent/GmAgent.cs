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

        var dbSummary = await DbSummaryBuilder.BuildAsync(dbContext, cancellationToken);

        // 历史上下文（深度注入）：序章 + 最近一条编年史，作为独立消息紧贴生成点。
        var openingHistory = string.Join("\n\n", new[]
        {
            string.IsNullOrWhiteSpace(prologue) ? null : $"【序章/前序故事 AM0000】\n{prologue}",
            string.IsNullOrWhiteSpace(lastChronicle) ? null : $"【上一回合编年史】\n{lastChronicle}"
        }.Where(s => s is not null));

        var response = await llmClient.SendChatAsync(
            new LlmRequest
            {
                AgentName = "GM",
                Options = AgentConfigResolver.ToLlmOptions(config),
                Messages =
                [
                    LlmMessage.System(config?.SystemPrompt ?? "你是Re:Zero桌游GM。"),
                    LlmMessage.User(promptComposer.ComposeGmOpening(state, profiles, slotList, ragContext, prologue, dbSummary, lastChronicle)),
                    LlmMessage.User(promptComposer.ComposeHistoryInjection(openingHistory)),
                    LlmMessage.User(promptComposer.ComposeGmOpeningThoughtGuide()),
                    LlmMessage.Assistant(PromptComposer.ThoughtPrefill)
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
        var roster = await BuildCombatRosterAsync(cancellationToken);

        var response = await llmClient.SendChatAsync(
            new LlmRequest
            {
                AgentName = "GM",
                Options = AgentConfigResolver.ToLlmOptions(config),
                Messages =
                [
                    LlmMessage.System(config?.SystemPrompt ?? "你是Re:Zero桌游GM。"),
                    LlmMessage.User(promptComposer.ComposeGmJudgement(turn, ragContext, charAttrs, roster)),
                    LlmMessage.Assistant(PromptComposer.ThoughtPrefill)
                ]
            },
            cancellationToken);

        return response.Content;
    }

    /// <summary>
    /// 生成「ID｜名字｜HP/MP/体力｜护甲｜技能」战斗名册，注入 GM 判定提示词。
    /// GM 叙事仍用名字，裁决战斗时用 #ID 指代攻防双方。
    /// </summary>
    private async Task<string> BuildCombatRosterAsync(CancellationToken cancellationToken)
    {
        var rows = new List<string>();

        var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (protagonist is not null)
        {
            rows.Add(FormatRosterRow(
                protagonist.CharId, protagonist.Name, protagonist.Hp, protagonist.MaxHp,
                protagonist.Mp, protagonist.MaxMp, protagonist.Stamina, protagonist.MaxStamina,
                protagonist.Armor, protagonist.SkillsJson));
        }

        var npcs = await dbContext.ImportantNpcs.AsNoTracking()
            .OrderBy(n => n.CharId)
            .ToListAsync(cancellationToken);
        foreach (var npc in npcs)
        {
            rows.Add(FormatRosterRow(
                npc.CharId, npc.Name, npc.Hp, npc.MaxHp,
                npc.Mp, npc.MaxMp, npc.Stamina, npc.MaxStamina,
                npc.Armor, npc.SkillsJson));
        }

        return rows.Count == 0 ? "（本场无在册角色）" : string.Join('\n', rows);
    }

    private static string FormatRosterRow(
        int charId, string name, int hp, int maxHp, int mp, int maxMp,
        int stamina, int maxStamina, int armor, string? skillsJson)
    {
        var id = charId > 0 ? $"#{charId}" : "#?";
        var skills = SkillsSerializer.Parse(skillsJson)
            .Where(s => s.Available)
            .Select(s => s.Name);
        var skillText = skills.Any() ? $"｜技能:{string.Join('/', skills)}" : "";
        return $"{id} = {name}｜HP {hp}/{maxHp}｜MP {mp}/{maxMp}｜体力 {stamina}/{maxStamina}｜护甲 {armor}{skillText}";
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

using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Llm;
using Re0Agent.Core.Services.Settings;

namespace Re0Agent.Core.Services.Agent;

public sealed class CharacterAgentService(
    Re0AgentDbContext dbContext,
    AgentConfigResolver configResolver,
    PromptComposer promptComposer,
    ILlmClient llmClient,
    IRagService ragService)
{
    public async Task<IReadOnlyList<CharacterAgentProfile>> LoadActiveProfilesAsync(
        CancellationToken cancellationToken = default)
    {
        var profiles = new List<CharacterAgentProfile>();
        var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

        profiles.Add(new CharacterAgentProfile
        {
            CharacterName = protagonist?.Name ?? "菜月昴",
            IsPlayerControlled = true,
            CurrentStateReference = protagonist is null ? "protagonist_info:missing" : "protagonist_info:1",
            WorldBookEntryKey = protagonist?.Name ?? "菜月昴"
        });

        var npcs = await dbContext.ImportantNpcs
            .AsNoTracking()
            .Where(npc => npc.PresenceStatus == "在场")
            .OrderBy(npc => npc.RowId)
            .ToListAsync(cancellationToken);

        foreach (var npc in npcs)
        {
            profiles.Add(new CharacterAgentProfile
            {
                CharacterName = npc.Name,
                IsPlayerControlled = false,
                CurrentStateReference = $"important_npc:{npc.RowId}",
                WorldBookEntryKey = npc.Name
            });
        }

        // NPC 先行动，主角最后行动。
        return profiles
            .Where(profile => !profile.IsPlayerControlled)
            .Concat(profiles.Where(profile => profile.IsPlayerControlled))
            .ToList();
    }

    public async Task<string> RunTurnAsync(
        GameRound round,
        CharacterAgentProfile profile,
        string? playerInstruction,
        CancellationToken cancellationToken = default)
    {
        var config = await configResolver.FindConfigAsync("Character", profile.CharacterName, cancellationToken);
        var memories = await dbContext.CharacterMemory
            .AsNoTracking()
            .Where(memory => memory.CharacterName == profile.CharacterName)
            .OrderByDescending(memory => memory.RowId)
            .Take(8)
            .ToListAsync(cancellationToken);

        var allowedCategories = await BuildAllowedCategoriesAsync(profile, cancellationToken);
        var ragContext = await ragService.QueryAsync(
            new RagQuery
            {
                Text = string.Join(' ', allowedCategories.Where(k => k.Contains(':')).Select(k => k[(k.IndexOf(':') + 1)..])),
                Chapter = round.Chapter,
                MaxNonConstantEntries = 8,
                MaxCharacters = 8_000,
                IncludeChapterEntries = false,
                AllowedCategories = allowedCategories
            },
            cancellationToken);

        var response = await llmClient.SendChatAsync(
            new LlmRequest
            {
                AgentName = profile.CharacterName,
                Options = AgentConfigResolver.ToLlmOptions(config),
                Messages =
                [
                    LlmMessage.System(config?.SystemPrompt ?? $"你是{profile.CharacterName}的专属角色Agent。"),
                    LlmMessage.User(promptComposer.ComposeCharacterTurn(profile, round, memories, playerInstruction, ragContext))
                ]
            },
            cancellationToken);

        return response.Content;
    }

    private async Task<IReadOnlyList<string>> BuildAllowedCategoriesAsync(
        CharacterAgentProfile profile,
        CancellationToken cancellationToken)
    {
        var categories = new List<string> { "world_settings" };

        // 角色自身
        var characterKey = profile.WorldBookEntryKey ?? profile.CharacterName;
        if (!string.IsNullOrWhiteSpace(characterKey))
        {
            categories.AddRange(
                (await ragService.ListAllEntriesAsync(cancellationToken))
                    .Select(WorldBookCategory.GetKey)
                    .Where(k => k.StartsWith("characters:", StringComparison.Ordinal)
                        && MatchesTerm(k["characters:".Length..], characterKey))
                    .Distinct(StringComparer.Ordinal));
        }

        // 所在地点
        var locationTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var state = await dbContext.GlobalStates.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        AddTerm(locationTerms, state?.CurrentLocation);
        AddTerm(locationTerms, state?.CurrentMinorRegion);
        AddTerm(locationTerms, state?.CurrentMajorRegion);

        if (profile.IsPlayerControlled)
        {
            var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
            AddTerm(locationTerms, protagonist?.LocationName);
        }
        else
        {
            var npcLocation = await dbContext.ImportantNpcs.AsNoTracking()
                .Where(npc => npc.Name == profile.CharacterName)
                .Select(npc => npc.LocationName)
                .FirstOrDefaultAsync(cancellationToken);
            AddTerm(locationTerms, npcLocation);
        }

        if (locationTerms.Count > 0)
        {
            categories.AddRange(
                (await ragService.ListAllEntriesAsync(cancellationToken))
                    .Select(WorldBookCategory.GetKey)
                    .Where(k => k.StartsWith("locations:", StringComparison.Ordinal)
                        && locationTerms.Any(t => MatchesTerm(k["locations:".Length..], t)))
                    .Distinct(StringComparer.Ordinal));
        }

        return categories;
    }

    private static bool MatchesTerm(string subject, string term) =>
        subject.Contains(term, StringComparison.OrdinalIgnoreCase)
        || term.Contains(subject, StringComparison.OrdinalIgnoreCase);

    private static void AddTerm(HashSet<string> terms, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) terms.Add(value.Trim());
    }
}

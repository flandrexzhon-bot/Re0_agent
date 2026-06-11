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
    private static readonly string[] BaseSettingCommentMarkers =
    [
        "基础设定",
        "基础&世界设定",
        "时间&历法设定",
        "货币&收入设定",
        "饮食&习惯设定",
        "玛娜&魔法设定",
        "权能&加护设定"
    ];

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
        var visibleRag = await BuildCharacterVisibleRagAsync(profile, cancellationToken);
        var ragContext = await ragService.QueryAsync(
            new RagQuery
            {
                Text = visibleRag.QueryText,
                Chapter = round.Chapter,
                MaxNonConstantEntries = visibleRag.NonConstantEntryIds.Count,
                MaxCharacters = 8_000,
                IncludeChapterEntries = false,
                AllowedConstantEntryIds = visibleRag.ConstantEntryIds,
                AllowedNonConstantEntryIds = visibleRag.NonConstantEntryIds
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

    private async Task<CharacterVisibleRag> BuildCharacterVisibleRagAsync(
        CharacterAgentProfile profile,
        CancellationToken cancellationToken)
    {
        var characterTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddTerm(characterTerms, profile.CharacterName);
        AddTerm(characterTerms, profile.WorldBookEntryKey);

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

        var entries = await ragService.ListAllEntriesAsync(cancellationToken);
        var constantEntryIds = entries
            .Where(IsBaseSettingEntry)
            .Select(entry => entry.Id)
            .ToArray();
        var nonConstantEntryIds = entries
            .Where(entry => !entry.Constant && !IsChapterSettingEntry(entry))
            .Where(entry =>
                (IsLocationSettingEntry(entry) && LocationEntryMatchesAnyTerm(entry, locationTerms))
                || (IsCharacterSettingEntry(entry) && CharacterEntryMatchesAnyTerm(entry, characterTerms)))
            .Select(entry => entry.Id)
            .ToArray();

        var queryTerms = characterTerms
            .Concat(locationTerms)
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .DefaultIfEmpty("基础设定");

        return new CharacterVisibleRag(
            string.Join(' ', queryTerms),
            constantEntryIds,
            nonConstantEntryIds);
    }

    private static void AddTerm(HashSet<string> terms, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            terms.Add(value.Trim());
        }
    }

    private static bool IsBaseSettingEntry(WorldBookEntry entry)
    {
        if (!entry.Constant || IsChapterSettingEntry(entry))
        {
            return false;
        }

        return BaseSettingCommentMarkers.Any(marker =>
            entry.Comment.Contains(marker, StringComparison.Ordinal));
    }

    private static bool IsChapterSettingEntry(WorldBookEntry entry)
    {
        return entry.Comment.Contains("章节设定", StringComparison.Ordinal)
            || entry.Comment.Contains("章(", StringComparison.Ordinal)
            || entry.Keys.Any(key => key.StartsWith("第", StringComparison.Ordinal) && key.EndsWith("章", StringComparison.Ordinal));
    }

    private static bool IsLocationSettingEntry(WorldBookEntry entry)
    {
        var comment = entry.Comment;
        return comment.Contains("·地点:", StringComparison.Ordinal)
            || comment.Contains("·城市:", StringComparison.Ordinal)
            || comment.Contains("地点:", StringComparison.Ordinal)
            || comment.Contains("城市:", StringComparison.Ordinal)
            || comment.Contains("地点：", StringComparison.Ordinal)
            || comment.Contains("城市：", StringComparison.Ordinal);
    }

    private static bool IsCharacterSettingEntry(WorldBookEntry entry)
    {
        var comment = entry.Comment;
        return comment.Contains("·人物:", StringComparison.Ordinal)
            || comment.Contains("·角色:", StringComparison.Ordinal)
            || comment.Contains("人物:", StringComparison.Ordinal)
            || comment.Contains("角色:", StringComparison.Ordinal)
            || comment.Contains("人物：", StringComparison.Ordinal)
            || comment.Contains("角色：", StringComparison.Ordinal);
    }

    private static bool LocationEntryMatchesAnyTerm(WorldBookEntry entry, IEnumerable<string> terms)
    {
        return terms.Any(term => EntrySubjectOrKeyMatchesTerm(entry, term));
    }

    private static bool CharacterEntryMatchesAnyTerm(WorldBookEntry entry, IEnumerable<string> terms)
    {
        return terms.Any(term => EntrySubjectOrKeyMatchesTerm(entry, term));
    }

    private static bool EntrySubjectOrKeyMatchesTerm(WorldBookEntry entry, string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return false;
        }

        var subject = ExtractEntrySubject(entry.Comment);
        return subject.Contains(term, StringComparison.OrdinalIgnoreCase)
            || term.Contains(subject, StringComparison.OrdinalIgnoreCase)
            || entry.Keys.Concat(entry.SecondaryKeys).Any(key =>
                key.Contains(term, StringComparison.OrdinalIgnoreCase)
                || term.Contains(key, StringComparison.OrdinalIgnoreCase));
    }

    private static string ExtractEntrySubject(string comment)
    {
        var separatorIndex = Math.Max(comment.LastIndexOf(':'), comment.LastIndexOf('：'));
        return separatorIndex < 0 ? comment.Trim() : comment[(separatorIndex + 1)..].Trim();
    }

    private sealed record CharacterVisibleRag(
        string QueryText,
        IReadOnlyCollection<int> ConstantEntryIds,
        IReadOnlyCollection<int> NonConstantEntryIds);
}

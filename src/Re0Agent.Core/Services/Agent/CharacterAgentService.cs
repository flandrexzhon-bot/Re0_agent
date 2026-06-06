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
        var ragContext = await ragService.QueryAsync(
            new RagQuery
            {
                Text = $"{profile.CharacterName} {profile.WorldBookEntryKey} {profile.CurrentStateReference} {round.GmOpening} {playerInstruction} {string.Join(' ', round.CharacterTurns.Select(turn => $"{turn.CharacterName} {turn.ActionText}"))}",
                Chapter = round.Chapter
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

        round.UsedFakeClient |= response.UsedFakeClient;
        return response.Content;
    }
}

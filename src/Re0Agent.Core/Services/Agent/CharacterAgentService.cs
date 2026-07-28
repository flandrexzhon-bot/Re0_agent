using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Llm;
using Re0Agent.Core.Services.Settings;

namespace Re0Agent.Core.Services.Agent;

public sealed class CharacterAgentService(
    Re0AgentDbContext dbContext,
    AgentConfigResolver configResolver,
    PromptComposer promptComposer,
    ILlmClient llmClient,
    IRagService ragService,
    RuntimeLorebookService runtimeLorebook)
{
    public async Task<IReadOnlyList<CharacterAgentProfile>> LoadActiveProfilesAsync(
        bool includeProtagonistAgent = true,
        CancellationToken cancellationToken = default)
    {
        var profiles = new List<CharacterAgentProfile>();
        var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (includeProtagonistAgent && protagonist is not null)
        {
            profiles.Add(new CharacterAgentProfile
            {
                CharacterId = $"protagonist:{protagonist.RowId}",
                CharacterName = protagonist.Name,
                IsPlayerControlled = false,
                CurrentStateReference = "protagonist_info:1",
                WorldBookEntryKey = protagonist.Name
            });
        }

        var npcs = await dbContext.ImportantNpcs.AsNoTracking().OrderBy(item => item.RowId).ToListAsync(cancellationToken);
        profiles.AddRange(npcs.Select(npc => new CharacterAgentProfile
        {
            CharacterId = $"npc:{npc.RowId}",
            CharacterName = npc.Name,
            IsPlayerControlled = false,
            CurrentStateReference = $"important_npc:{npc.RowId}",
            WorldBookEntryKey = npc.Name
        }));
        return profiles;
    }

    public async Task<string> GenerateActionAsync(
        ActorBrief actorBrief,
        CharacterAgentProfile profile,
        CancellationToken cancellationToken = default)
    {
        var config = await configResolver.FindConfigAsync("Character", profile.CharacterName, cancellationToken);
        var ragContext = await ragService.QueryAsync(new RagQuery
        {
            Text = $"{profile.CharacterName} {actorBrief.ContextEvent.Content}",
            Chapter = await ReadChapterAsync(cancellationToken),
            MaxNonConstantEntries = 8,
            IncludeChapterEntries = false
        }, cancellationToken);
        var importedLore = await runtimeLorebook.ActivateAsync($"{profile.CharacterName}\n{actorBrief.ContextEvent.Content}", cancellationToken: cancellationToken);
        if (!string.IsNullOrWhiteSpace(importedLore))
        {
            ragContext = new RagContext { Matches = ragContext.Matches, Content = ragContext.Content + "\n\n" + importedLore };
        }
        var summary = await DbSummaryBuilder.BuildForCharacterAsync(
            dbContext, profile.CharacterName, isPlayerControlled: profile.CharacterId.StartsWith("protagonist:", StringComparison.Ordinal), cancellationToken);
        var response = await llmClient.SendChatAsync(new LlmRequest
        {
            AgentName = profile.CharacterName,
            Options = AgentConfigResolver.ToLlmOptions(config),
            Messages =
            [
                LlmMessage.System(config?.SystemPrompt ?? $"你是{profile.CharacterName}的角色 Agent。"),
                LlmMessage.User(promptComposer.ComposeCharacterAction(profile, actorBrief.ContextEvent, actorBrief.PrivateMemories, actorBrief.Opportunity, ragContext, summary)),
                LlmMessage.User(promptComposer.ComposeCharacterTurnThoughtGuide()),
                LlmMessage.Assistant(PromptComposer.ThoughtPrefill)
            ]
        }, cancellationToken);
        return response.Content;
    }

    private async Task<int> ReadChapterAsync(CancellationToken cancellationToken) =>
        await dbContext.GlobalStates.AsNoTracking().Select(item => (int?)item.CurrentChapter)
            .FirstOrDefaultAsync(cancellationToken) ?? 1;
}

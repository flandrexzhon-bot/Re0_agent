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
    IRagService ragService)
{
    public async Task<string> CreateOpeningAsync(
        GameRound round,
        IReadOnlyList<CharacterAgentProfile> profiles,
        CancellationToken cancellationToken = default)
    {
        var config = await configResolver.FindConfigAsync("GM", "GM", cancellationToken);
        var state = await dbContext.GlobalStates.FindAsync([1], cancellationToken);
        var ragContext = await ragService.QueryAsync(
            new RagQuery
            {
                Text = $"{state?.CurrentMajorRegion} {state?.CurrentMinorRegion} {state?.CurrentLocation} {round.PlayerInput} {string.Join(' ', profiles.Select(profile => profile.CharacterName))}",
                Chapter = round.Chapter
            },
            cancellationToken);

        var prologue = await dbContext.Chronicle.AsNoTracking()
            .Where(c => c.CodeIndex == "AM0000")
            .Select(c => c.ChronicleText)
            .FirstOrDefaultAsync(cancellationToken);

        var response = await llmClient.SendChatAsync(
            new LlmRequest
            {
                AgentName = "GM",
                Options = AgentConfigResolver.ToLlmOptions(config),
                Messages =
                [
                    LlmMessage.System(config?.SystemPrompt ?? "你是Re:Zero桌游GM。"),
                    LlmMessage.User(promptComposer.ComposeGmOpening(state, profiles, ragContext, prologue))
                ]
            },
            cancellationToken);

        round.UsedFakeClient |= response.UsedFakeClient;
        return response.Content;
    }

    public async Task<string> JudgeTurnAsync(
        GameRound round,
        CharacterTurn turn,
        CancellationToken cancellationToken = default)
    {
        var config = await configResolver.FindConfigAsync("GM", "GM", cancellationToken);
        var ragContext = await ragService.QueryAsync(
            new RagQuery
            {
                Text = $"{turn.CharacterName} {turn.ActionText} {round.GmOpening}",
                Chapter = round.Chapter
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
                    LlmMessage.User(promptComposer.ComposeGmJudgement(turn, ragContext))
                ]
            },
            cancellationToken);

        round.UsedFakeClient |= response.UsedFakeClient;
        return response.Content;
    }

    public async Task<string> SummarizeAsync(
        GameRound round,
        CancellationToken cancellationToken = default)
    {
        var config = await configResolver.FindConfigAsync("GM", "GM", cancellationToken);
        var ragContext = await ragService.QueryAsync(
            new RagQuery
            {
                Text = $"{round.GmOpening} {string.Join(' ', round.CharacterTurns.Select(turn => $"{turn.CharacterName} {turn.ActionText} {turn.GmJudgement}"))}",
                Chapter = round.Chapter
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

        round.UsedFakeClient |= response.UsedFakeClient;
        return response.Content;
    }
}

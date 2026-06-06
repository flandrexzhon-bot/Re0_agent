using Re0Agent.Core.Database;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Llm;

namespace Re0Agent.Core.Services.Agent;

public sealed class GmAgent(
    Re0AgentDbContext dbContext,
    AgentConfigResolver configResolver,
    PromptComposer promptComposer,
    ILlmClient llmClient)
{
    public async Task<string> CreateOpeningAsync(
        GameRound round,
        IReadOnlyList<CharacterAgentProfile> profiles,
        CancellationToken cancellationToken = default)
    {
        var config = await configResolver.FindConfigAsync("GM", "GM", cancellationToken);
        var state = await dbContext.GlobalStates.FindAsync([1], cancellationToken);
        var response = await llmClient.SendChatAsync(
            new LlmRequest
            {
                AgentName = "GM",
                Options = AgentConfigResolver.ToLlmOptions(config),
                Messages =
                [
                    LlmMessage.System(config?.SystemPrompt ?? "你是Re:Zero桌游GM。"),
                    LlmMessage.User(promptComposer.ComposeGmOpening(state, profiles))
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
        var response = await llmClient.SendChatAsync(
            new LlmRequest
            {
                AgentName = "GM",
                Options = AgentConfigResolver.ToLlmOptions(config),
                Messages =
                [
                    LlmMessage.System(config?.SystemPrompt ?? "你是Re:Zero桌游GM。"),
                    LlmMessage.User(promptComposer.ComposeGmJudgement(turn))
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
        var response = await llmClient.SendChatAsync(
            new LlmRequest
            {
                AgentName = "GM",
                Options = AgentConfigResolver.ToLlmOptions(config),
                Messages =
                [
                    LlmMessage.System(config?.SystemPrompt ?? "你是Re:Zero桌游GM。"),
                    LlmMessage.User(promptComposer.ComposeGmSummary(round))
                ]
            },
            cancellationToken);

        round.UsedFakeClient |= response.UsedFakeClient;
        return response.Content;
    }
}

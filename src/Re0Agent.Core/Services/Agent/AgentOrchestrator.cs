using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Services.Database;

namespace Re0Agent.Core.Services.Agent;

public sealed class AgentOrchestrator(
    Re0AgentDbContext dbContext,
    CharacterAgentService characterAgentService,
    EventSingleWriter eventWriter,
    ObservabilityComputer observabilityComputer,
    InformationGate informationGate,
    WorldAffordanceValidator affordanceValidator,
    RuleResolver ruleResolver,
    StateProjectionService stateProjectionService,
    WorldCancellationRegistry cancellationRegistry)
{
    public async Task<TimelineEvent> SubmitPlayerInputAsync(
        string content,
        CancellationToken cancellationToken = default)
    {
        var session = await ActiveSessionAsync(cancellationToken);
        if (session.GameMode == "Theater")
        {
            var direction = await eventWriter.CommitAsync(
                session.SessionId, "PlayerDirection", content, triggerCause: "player_direction", cancellationToken: cancellationToken);
            dbContext.PendingDirections.Add(new PendingDirection
            {
                SessionId = session.SessionId,
                SourceEventId = direction.EventId,
                Content = content,
                Preconditions = "[]",
                EarliestWorldTime = session.WorldClockAnchor ?? DateTimeOffset.UtcNow.ToString("O"),
                Status = "Pending",
                CreatedAt = DateTimeOffset.UtcNow.ToString("O")
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return direction;
        }
        var sceneId = await dbContext.GlobalStates.AsNoTracking().Select(item => item.CurrentLocation)
            .FirstOrDefaultAsync(cancellationToken);
        var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("活动会话缺少主角投影。");
        var actorId = $"protagonist:{protagonist.RowId}";
        var observers = await observabilityComputer.ComputeAsync(sceneId, actorId, targetId: null, cancellationToken);
        var action = await eventWriter.CommitAsync(
            session.SessionId,
            "PlayerInput",
            content,
            sceneId: sceneId,
            actorId: actorId,
            observers: observers,
            triggerCause: "player_submission",
            cancellationToken: cancellationToken);
        return action;
    }

    public async Task<TimelineEvent> RunCharacterActionAsync(
        string characterId,
        TimelineEvent contextEvent,
        string? actorOpportunity = null,
        CancellationToken cancellationToken = default)
    {
        cancellationRegistry.ResetForeground((await ActiveSessionAsync(cancellationToken)).SessionId);
        var session = await ActiveSessionAsync(cancellationToken);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, cancellationRegistry.ForegroundToken(session.SessionId));
        cancellationToken = linkedCancellation.Token;
        var profiles = await characterAgentService.LoadActiveProfilesAsync(
            includeProtagonistAgent: session.GameMode == "Theater", cancellationToken);
        var profile = profiles.SingleOrDefault(item => item.CharacterId == characterId)
            ?? throw new InvalidOperationException("角色不在当前会话的可行动集合中。");
        var actorBrief = await informationGate.BuildActorBriefAsync(
            session, profile.CharacterId, contextEvent.EventId, actorOpportunity, cancellationToken);
        await affordanceValidator.ValidateAsync(profile, actorBrief, cancellationToken);
        var content = await characterAgentService.GenerateActionAsync(actorBrief, profile, cancellationToken);
        var observers = await observabilityComputer.ComputeAsync(
            actorBrief.SceneId,
            profile.CharacterId,
            targetId: null,
            cancellationToken);
        var action = await eventWriter.CommitAsync(
            session.SessionId,
            "CharacterAction",
            content,
            sceneId: actorBrief.SceneId,
            actorId: profile.CharacterId,
            causalParentEventId: contextEvent.EventId,
            triggerCause: "character_agency",
            observers: observers,
            cancellationToken: cancellationToken);
        var resolution = await ruleResolver.ResolveIfRequestedAsync(session.SessionId, action, cancellationToken);
        await stateProjectionService.ProposeAndCommitAsync(session.SessionId, resolution ?? action, cancellationToken);
        return action;
    }

    private async Task<ChatSession> ActiveSessionAsync(CancellationToken cancellationToken) =>
        await dbContext.ChatSessions.SingleOrDefaultAsync(item => item.IsActive == 1, cancellationToken)
        ?? throw new InvalidOperationException("没有活动会话。");
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Services.Agent;
using Re0Agent.Core.Services.Database;
using Re0Agent.Core.Models;

namespace Re0Agent.Core.Services.Settings;

public sealed class ImportedGreetingService(
    Re0AgentDbContext dbContext,
    EventSingleWriter eventWriter,
    ObservabilityComputer observabilityComputer,
    InitialSceneProposalService proposalService,
    WorldAffordanceValidator affordanceValidator)
{
    public async Task<string> CommitAsync(
        int sessionId,
        int sourceId,
        int alternateGreetingIndex = -1,
        CancellationToken cancellationToken = default)
    {
        var card = await dbContext.CharacterCardSources.SingleAsync(item => item.SourceId == sourceId, cancellationToken);
        var greetings = JsonSerializer.Deserialize<string[]>(card.AlternateGreetings) ?? [];
        var greeting = alternateGreetingIndex >= 0 && alternateGreetingIndex < greetings.Length
            ? greetings[alternateGreetingIndex]
            : card.FirstMessage ?? string.Empty;
        if (string.IsNullOrEmpty(greeting))
        {
            throw new InvalidOperationException("角色卡没有可展示的 first_mes 或备用问候。");
        }
        var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("提交 ImportedGreeting 前必须存在主角投影。");
        var scene = await dbContext.GlobalStates.AsNoTracking().Select(item => item.CurrentLocation)
            .FirstOrDefaultAsync(cancellationToken);
        var observers = await observabilityComputer.ComputeAsync(scene, $"protagonist:{protagonist.RowId}", null, cancellationToken);
        var greetingEvent = await eventWriter.CommitAsync(
            sessionId,
            "ImportedGreeting",
            greeting,
            sceneId: scene,
            actorId: $"card:{sourceId}",
            observers: observers,
            triggerCause: "authored_prologue",
            cancellationToken: cancellationToken);
        var proposal = proposalService.Build(card, protagonist.Name);
        affordanceValidator.ValidateInitialSceneProposal(proposal, protagonist.Name);
        var now = DateTimeOffset.UtcNow.ToString("O");
        var sceneExists = await dbContext.SceneStates.AnyAsync(item => item.SceneId == proposal.SceneId, cancellationToken);
        var changes = new StateChangeSet
        ([
            new StateChangeCommand(
                Guid.NewGuid().ToString("N"), "scene_states", proposal.SceneId, sceneExists ? "update" : "insert", sceneExists ? null : 0,
                new Dictionary<string, JsonElement>
                {
                    ["is_foreground"] = JsonSerializer.SerializeToElement(1),
                    ["summary"] = JsonSerializer.SerializeToElement(JsonSerializer.Serialize(new { proposal.SourceKey, proposal.PresentCharacterNames })),
                    ["last_world_time"] = JsonSerializer.SerializeToElement(proposal.LogicalWorldTime)
                }, "imported_scene_proposal"),
            new StateChangeCommand(
                Guid.NewGuid().ToString("N"), "global_state", "1", "update", null,
                new Dictionary<string, JsonElement>
                {
                    ["current_location"] = JsonSerializer.SerializeToElement(proposal.SceneId)
                }, "imported_scene_proposal"),
            new StateChangeCommand(
                Guid.NewGuid().ToString("N"), "protagonist_info", protagonist.RowId.ToString(), "update", null,
                new Dictionary<string, JsonElement>
                {
                    ["location_name"] = JsonSerializer.SerializeToElement(proposal.SceneId)
                }, "imported_scene_proposal")
        ]);
        await eventWriter.CommitAsync(
            sessionId,
            "InitialSceneProposal",
            JsonSerializer.Serialize(proposal),
            stateChangeSet: changes,
            triggerCause: "imported_card_initial_scene",
            causalParentEventId: greetingEvent.EventId,
            cancellationToken: cancellationToken);
        return greetingEvent.EventId;
    }
}

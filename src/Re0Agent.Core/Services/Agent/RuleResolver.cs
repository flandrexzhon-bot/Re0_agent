using System.Text.Json;
using System.Text.RegularExpressions;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Services.Database;
using Re0Agent.Core.Services.Dice;

namespace Re0Agent.Core.Services.Agent;

public sealed partial class RuleResolver(DiceEngine diceEngine, EventSingleWriter eventWriter, ObservabilityComputer observabilityComputer)
{
    public async Task<TimelineEvent?> ResolveIfRequestedAsync(int sessionId, TimelineEvent action, CancellationToken cancellationToken = default)
    {
        var match = JudgementRegex().Match(action.Content);
        if (!match.Success)
        {
            return null;
        }
        var result = await diceEngine.ExecuteAsync(match.Groups["command"].Value, cancellationToken);
        var observers = await observabilityComputer.ComputeAsync(action.SceneId, action.ActorId, action.TargetId, cancellationToken);
        return await eventWriter.CommitAsync(
            sessionId, "RuleResolution", JsonSerializer.Serialize(result), sceneId: action.SceneId,
            actorId: action.ActorId, targetId: action.TargetId, observers: observers,
            triggerCause: "rule_resolution", causalParentEventId: action.EventId, cancellationToken: cancellationToken);
    }

    [GeneratedRegex("【判定】\\s*(?<command>[^\\r\\n]+)", RegexOptions.CultureInvariant)]
    private static partial Regex JudgementRegex();
}

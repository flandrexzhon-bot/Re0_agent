using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Database;

namespace Re0Agent.Core.Services.Agent;

public sealed class KnowledgePropagationService(Re0AgentDbContext dbContext, EventSingleWriter eventWriter)
{
    private const int MaxTransfersPerEvent = 4;

    public async Task<int> PropagateAsync(int sessionId, string contactEventId, CancellationToken cancellationToken = default)
    {
        var contact = await dbContext.TimelineEvents.AsNoTracking().SingleAsync(item => item.EventId == contactEventId, cancellationToken);
        var direct = RevealQueueProjector.DeserializeIds(contact.DirectObservers).ToHashSet(StringComparer.Ordinal);
        if (direct.Count == 0) return 0;
        var sources = await dbContext.TimelineEvents.AsNoTracking().Where(item => item.BranchId == contact.BranchId
                && item.Status == "Committed" && item.Sequence < contact.Sequence && item.PotentialLearners != "[]")
            .OrderByDescending(item => item.Sequence).Take(64).ToListAsync(cancellationToken);
        var transferred = 0;
        foreach (var source in sources)
        {
            IReadOnlyList<PotentialLearner> learners;
            try { learners = JsonSerializer.Deserialize<PotentialLearner[]>(source.PotentialLearners) ?? []; }
            catch (JsonException) { continue; }
            foreach (var learner in learners.Where(item => direct.Contains(item.CharacterId)))
            {
                if (!ConditionSatisfied(learner, source, contact, direct)) continue;
                var signature = $"\"sourceEventId\":\"{source.EventId}\",\"learnerId\":\"{learner.CharacterId}\"";
                if (await dbContext.TimelineEvents.AsNoTracking().AnyAsync(item => item.BranchId == contact.BranchId
                    && item.EventType == "KnowledgeTransferred" && item.Content.Contains(signature), cancellationToken)) continue;
                var observers = new DirectObserversResult(
                    [learner.CharacterId],
                    new Dictionary<string, string>(StringComparer.Ordinal) { [learner.CharacterId] = learner.Channel },
                    [],
                    true);
                var content = $"{{{signature},\"channel\":{JsonSerializer.Serialize(learner.Channel)}}}";
                await eventWriter.CommitAsync(
                    sessionId,
                    "KnowledgeTransferred",
                    content,
                    sceneId: contact.SceneId,
                    actorId: contact.ActorId,
                    targetId: learner.CharacterId,
                    observers: observers,
                    causalParentEventId: contact.EventId,
                    triggerCause: "potential_learner_condition_satisfied",
                    stateChangeSet: MemoryChange(learner.CharacterId, source.Content),
                    cancellationToken: cancellationToken);
                transferred++;
                if (transferred >= MaxTransfersPerEvent) return transferred;
            }
        }
        return transferred;
    }

    private static bool ConditionSatisfied(
        PotentialLearner learner,
        Re0Agent.Core.Entities.TimelineEvent source,
        Re0Agent.Core.Entities.TimelineEvent contact,
        IReadOnlySet<string> contactObservers) => learner.EarliestCondition switch
    {
        "after_scene_entry" => source.SceneId == contact.SceneId,
        "after_report_or_encounter" => (contact.EventType is "CharacterAction" or "KeplerNarration")
            && source.ActorId is not null && contactObservers.Contains(source.ActorId),
        "after_contact_or_investigation" => contact.EventType == "Investigation"
            || contact.EventType == "CharacterAction" && source.ActorId is not null && contactObservers.Contains(source.ActorId),
        _ => false
    };

    private static StateChangeSet MemoryChange(string ownerId, string memoryText) => new(
    [
        new StateChangeCommand(
            Guid.NewGuid().ToString("N"),
            "character_memory",
            $"auto:{Guid.NewGuid():N}",
            "insert",
            null,
            new Dictionary<string, JsonElement>
            {
                ["owner_character_id"] = JsonSerializer.SerializeToElement(ownerId),
                ["memory_text"] = JsonSerializer.SerializeToElement(memoryText),
                ["memory_type"] = JsonSerializer.SerializeToElement("learned_fact"),
                ["retain_on_rewind"] = JsonSerializer.SerializeToElement(0),
                ["created_at"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.ToString("O"))
            },
            "knowledge_transfer")
    ]);
}

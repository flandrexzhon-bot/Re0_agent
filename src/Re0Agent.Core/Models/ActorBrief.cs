using Re0Agent.Core.Entities;

namespace Re0Agent.Core.Models;

public sealed record ActorBrief(
    string CharacterId,
    string? SceneId,
    TimelineEvent ContextEvent,
    IReadOnlyList<TimelineEvent> DirectlyObservedEvents,
    IReadOnlyList<CharacterMemory> PrivateMemories,
    string? Opportunity);

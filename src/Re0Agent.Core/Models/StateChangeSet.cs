using System.Text.Json;

namespace Re0Agent.Core.Models;

public sealed record StateChangeSet(
    IReadOnlyList<StateChangeCommand> Commands);

public sealed record StateChangeCommand(
    string CommandId,
    string Projection,
    string EntityId,
    string OperationType,
    long? ExpectedVersion,
    IReadOnlyDictionary<string, JsonElement> FieldChanges,
    string ValidationResult);

public sealed record WorldRewindRecord(
    int SavePointId,
    string RollbackScope,
    IReadOnlyList<string> MemoryRetainers,
    int NewWorldEpoch,
    string CommittedEventId);

public sealed record PotentialLearner(
    string CharacterId,
    string Channel,
    string EarliestCondition);

public sealed record DirectObserversResult(
    IReadOnlyList<string> DirectObserverIds,
    IReadOnlyDictionary<string, string> ObserverChannels,
    IReadOnlyList<PotentialLearner> PotentialLearners,
    bool ObservabilityComputed);

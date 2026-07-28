using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;

namespace Re0Agent.Core.Services.Database;

public sealed class EventSingleWriter(
    Re0AgentDbContext dbContext,
    StateChangeSetValidator validator,
    ProjectionCommandApplier projectionCommandApplier,
    ProjectionCheckpointService checkpointService,
    Re0Agent.Core.Services.Agent.DirectorPulseScheduler pulseScheduler,
    Re0Agent.Core.Services.Agent.RevealQueueProjector revealQueueProjector)
{
    public async Task<TimelineEvent> CommitAsync(
        int sessionId,
        string eventType,
        string content,
        string? sceneId = null,
        string? actorId = null,
        string? targetId = null,
        StateChangeSet? stateChangeSet = null,
        DirectObserversResult? observers = null,
        string? triggerCause = null,
        string? causalParentEventId = null,
        string? pacingMetadata = null,
        string? eventId = null,
        IReadOnlyList<string>? revealedEventCursors = null,
        string eventStatus = "Committed",
        CancellationToken cancellationToken = default)
    {
        var session = await dbContext.ChatSessions.SingleAsync(item => item.SessionId == sessionId, cancellationToken);
        if (string.IsNullOrWhiteSpace(session.CurrentBranchId))
        {
            throw new InvalidOperationException("会话缺少当前时间线分支。");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var nextSequence = (await dbContext.TimelineEvents
            .Where(item => item.BranchId == session.CurrentBranchId && item.Sequence != null)
            .MaxAsync(item => (long?)item.Sequence, cancellationToken) ?? 0) + 1;
        var now = DateTimeOffset.UtcNow.ToString("O");
        var computedObservers = observers ?? new DirectObserversResult([], new Dictionary<string, string>(), [], true);
        if (!computedObservers.ObservabilityComputed)
        {
            throw new InvalidOperationException("事件必须在提交前完成可观察性计算。");
        }
        if (observers is null && !IsNonObservable(eventType))
        {
            throw new InvalidOperationException("可感知事件必须提供 direct_observers。");
        }
        var eventRecordId = eventId ?? Guid.NewGuid().ToString("N");
        var worldTime = ReadWorldTime(session);
        if (stateChangeSet is not null)
        {
            stateChangeSet = validator.Validate(NormalizeMemoryCommands(
                stateChangeSet, eventRecordId, session.CurrentWorldEpoch, worldTime, computedObservers));
        }
        var eventRecord = new TimelineEvent
        {
            EventId = eventRecordId,
            BranchId = session.CurrentBranchId,
            Sequence = nextSequence,
            WorldEpoch = session.CurrentWorldEpoch,
            SceneId = sceneId,
            ActorId = actorId,
            TargetId = targetId,
            EventType = eventType,
            Content = content,
            StateChangeSet = stateChangeSet is null ? null : JsonSerializer.Serialize(stateChangeSet),
            DirectObservers = JsonSerializer.Serialize(computedObservers.DirectObserverIds),
            PotentialLearners = JsonSerializer.Serialize(computedObservers.PotentialLearners),
            ObservabilityComputed = computedObservers.ObservabilityComputed ? 1 : 0,
            VisibilityScope = JsonSerializer.Serialize(new
            {
                direct = computedObservers.DirectObserverIds,
                potential = computedObservers.PotentialLearners.Select(item => item.CharacterId)
            }),
            RevealedEventCursors = JsonSerializer.Serialize(revealedEventCursors ?? []),
            Status = eventStatus is "Draft" or "Committed" or "Interrupted" or "Superseded" ? eventStatus : throw new ArgumentOutOfRangeException(nameof(eventStatus)),
            PacingMetadata = pacingMetadata,
            TriggerCause = triggerCause,
            CausalParentEventId = causalParentEventId,
            RealCreatedAt = now,
            WorldTime = worldTime
        };
        dbContext.TimelineEvents.Add(eventRecord);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (stateChangeSet is not null)
        {
            await projectionCommandApplier.ApplyAsync(eventRecord.EventId, stateChangeSet, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        if (eventType == "InitialProjection" || nextSequence % 50 == 0)
        {
            await checkpointService.CaptureAsync(eventRecord.BranchId, nextSequence, eventRecord.WorldEpoch, cancellationToken);
        }
        await pulseScheduler.EnqueueAfterCommitAsync(sessionId, eventRecord, cancellationToken);
        await revealQueueProjector.EnqueueAfterCommitAsync(sessionId, eventRecord, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return eventRecord;
    }

    private static string ReadWorldTime(ChatSession session) =>
        string.IsNullOrWhiteSpace(session.WorldClockAnchor)
            ? DateTimeOffset.UtcNow.ToString("O")
            : session.WorldClockAnchor;

    private static StateChangeSet NormalizeMemoryCommands(
        StateChangeSet proposed,
        string eventId,
        int worldEpoch,
        string worldTime,
        DirectObserversResult observers)
    {
        var commands = proposed.Commands.Select(command =>
        {
            if (command.Projection != "character_memory")
            {
                return command;
            }
            if (!command.FieldChanges.TryGetValue("owner_character_id", out var ownerValue)
                || ownerValue.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(ownerValue.GetString()))
            {
                throw new InvalidOperationException("角色记忆必须指定所有者。");
            }
            var ownerId = ownerValue.GetString()!;
            if (!observers.DirectObserverIds.Contains(ownerId, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("角色记忆所有者不在该事件的 direct_observers 中。");
            }

            var fields = new Dictionary<string, JsonElement>(command.FieldChanges, StringComparer.Ordinal)
            {
                ["source_event_id"] = JsonSerializer.SerializeToElement(eventId),
                ["world_time"] = JsonSerializer.SerializeToElement(worldTime),
                ["world_epoch"] = JsonSerializer.SerializeToElement(worldEpoch),
                ["observation_channel"] = JsonSerializer.SerializeToElement(
                    observers.ObserverChannels.GetValueOrDefault(ownerId, "direct")),
                ["confidence"] = JsonSerializer.SerializeToElement("确知"),
                ["created_at"] = JsonSerializer.SerializeToElement(DateTimeOffset.UtcNow.ToString("O")),
                ["visibility_scope"] = JsonSerializer.SerializeToElement(JsonSerializer.Serialize(new
                {
                    direct = observers.DirectObserverIds,
                    potential = observers.PotentialLearners.Select(item => item.CharacterId)
                }))
            };
            if (!fields.ContainsKey("memory_type")) fields["memory_type"] = JsonSerializer.SerializeToElement("event");
            if (!fields.ContainsKey("retain_on_rewind")) fields["retain_on_rewind"] = JsonSerializer.SerializeToElement(0);
            return command with { FieldChanges = fields };
        }).ToList();
        return new StateChangeSet(commands);
    }

    private static bool IsNonObservable(string eventType) => eventType is
        "InitialProjection" or "WorldRewindCommitted" or "DirectorPlan" or "RuntimeControl" or "ProjectionCheckpoint"
        or "InputActivityStarted" or "InputActivityEnded" or "SttStarted" or "SttEnded" or "PlayerDirection"
        or "PlayerDirectionRealizing" or "PlayerDirectionCompleted" or "PlayerDirectionBlocked" or "DirectionSceneOpportunity"
        or "DistantWorldAdvance" or "RevealCommitted" or "InitialSceneProposal" or "DirectorReflection";
}

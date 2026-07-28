using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;

namespace Re0Agent.Core.Services.Agent;

public sealed record ContinuousModeDiagnostics(
    int MissingObservabilityCount,
    int IllegalMemorySourceCount,
    int MustRevealDeadlineViolations,
    int RevealQueueStatusMismatches,
    int PendingSchedulerJobs,
    double ForegroundLagSeconds,
    double BackpressureFactor,
    int InputAdmissionCount,
    int InputAdmissionLimit,
    int PendingRevealCount,
    int FailedPulseCount,
    IReadOnlyList<string> FailedHardGates)
{
    public bool Passed => FailedHardGates.Count == 0;
}

public sealed class ContinuousModeDiagnosticsService(Re0AgentDbContext dbContext)
{
    public const int MaximumPendingSchedulerJobs = 64;
    public const int MaximumPendingReveals = 128;
    public const double MaximumForegroundLagSeconds = 120;

    public async Task<ContinuousModeDiagnostics> EvaluateAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        var session = await dbContext.ChatSessions.AsNoTracking().SingleAsync(item => item.SessionId == sessionId, cancellationToken);
        var runtime = await dbContext.WorldRuntimeStates.AsNoTracking().SingleAsync(item => item.SessionId == sessionId, cancellationToken);
        var branchId = session.CurrentBranchId ?? throw new InvalidOperationException("会话缺少当前分支。");
        var events = await dbContext.TimelineEvents.AsNoTracking().Where(item => item.BranchId == branchId && item.Status == "Committed")
            .ToListAsync(cancellationToken);
        var missingObservability = events.Count(item => item.ObservabilityComputed != 1);
        var byId = events.ToDictionary(item => item.EventId, StringComparer.Ordinal);
        var memories = await dbContext.CharacterMemory.AsNoTracking().ToListAsync(cancellationToken);
        var illegalMemorySources = memories.Count(memory => !byId.TryGetValue(memory.SourceEventId, out var source)
            || !RevealQueueProjector.DeserializeIds(source.DirectObservers).Contains(memory.OwnerCharacterId, StringComparer.Ordinal));
        var queue = await dbContext.RevealQueue.AsNoTracking().Where(item => item.SessionId == sessionId).ToListAsync(cancellationToken);
        var logicalNow = DateTimeOffset.TryParse(session.WorldClockAnchor, out var parsed) ? parsed : DateTimeOffset.UtcNow;
        var mustRevealViolations = queue.Count(item => item.Status == "Pending" && item.MustReveal == 1
            && DateTimeOffset.TryParse(item.LatestRevealWorldTime, out var deadline) && deadline < logicalNow);
        var revealedIds = events.SelectMany(item => RevealQueueProjector.DeserializeIds(item.RevealedEventCursors))
            .ToHashSet(StringComparer.Ordinal);
        var coalescedIds = queue.SelectMany(item => RevealQueueProjector.DeserializeIds(item.CoalescedEventIds))
            .ToHashSet(StringComparer.Ordinal);
        var queueMismatches = queue.Count(item => item.Status != ExpectedStatus(item.EventId, revealedIds, coalescedIds));
        var failedPulseCount = await dbContext.DirectorPulses.CountAsync(item => item.SessionId == sessionId && item.Status == "Failed", cancellationToken);
        var failures = new List<string>();
        if (missingObservability > 0) failures.Add("observability_missing");
        if (illegalMemorySources > 0) failures.Add("memory_visibility_violation");
        if (mustRevealViolations > 0) failures.Add("must_reveal_deadline_violation");
        if (queueMismatches > 0) failures.Add("reveal_queue_rebuild_mismatch");
        if (runtime.ForegroundPendingCount > MaximumPendingSchedulerJobs) failures.Add("scheduler_backlog_over_limit");
        if (runtime.ForegroundLagSeconds > MaximumForegroundLagSeconds) failures.Add("foreground_lag_over_limit");
        if (runtime.GenerationBackpressureFactor is < .1 or > 1) failures.Add("backpressure_out_of_range");
        if (session.InputSlowFactor < .999 && runtime.InputEventCount > runtime.ForegroundAdmissionLimit) failures.Add("input_admission_over_limit");
        if (queue.Count(item => item.Status == "Pending" && item.MustReveal == 0) > MaximumPendingReveals) failures.Add("reveal_queue_over_limit");
        if (failedPulseCount > 0) failures.Add("director_pulse_failed");
        return new ContinuousModeDiagnostics(
            missingObservability,
            illegalMemorySources,
            mustRevealViolations,
            queueMismatches,
            runtime.ForegroundPendingCount,
            runtime.ForegroundLagSeconds,
            runtime.GenerationBackpressureFactor,
            runtime.InputEventCount,
            runtime.ForegroundAdmissionLimit,
            queue.Count(item => item.Status == "Pending"),
            failedPulseCount,
            failures);
    }

    private static string ExpectedStatus(string eventId, IReadOnlySet<string> revealed, IReadOnlySet<string> coalesced) =>
        revealed.Contains(eventId) ? "Revealed" : coalesced.Contains(eventId) ? "Coalesced" : "Pending";
}

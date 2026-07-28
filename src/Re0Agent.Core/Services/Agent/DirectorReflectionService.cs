using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Database;

namespace Re0Agent.Core.Services.Agent;

public enum ReflectionPriority { None, Medium, High }

public sealed record ReflectionTrigger(bool ShouldReflect, string Reason, ReflectionPriority Priority);

public sealed class DirectorReflectionService(
    Re0AgentDbContext dbContext,
    SceneDirector sceneDirector,
    EventSingleWriter eventWriter)
{
    public async Task<BeatPlan?> ReflectIfTriggeredAsync(
        int sessionId,
        string triggerEventId,
        string triggerType,
        CancellationToken cancellationToken = default)
    {
        var session = await dbContext.ChatSessions.SingleAsync(item => item.SessionId == sessionId, cancellationToken);
        if (string.IsNullOrWhiteSpace(session.CurrentBranchId)) return null;
        var latestVersion = await dbContext.DirectorPlanVersions.AsNoTracking()
            .Where(item => item.BranchId == session.CurrentBranchId).OrderByDescending(item => item.VersionId)
            .FirstOrDefaultAsync(cancellationToken);
        var pacingState = await LoadPacingStateAsync(session.CurrentBranchId, cancellationToken);
        var activeThreadCount = await dbContext.StoryThreads.CountAsync(item => item.Status == "Active", cancellationToken);
        var revealConflict = await HasRevealConflictAsync(sessionId, session.WorldClockAnchor, cancellationToken);
        var trigger = EvaluateTrigger(triggerType, latestVersion is not null, pacingState, latestVersion?.PacePhase,
            activeThreadCount, revealConflict);
        if (!trigger.ShouldReflect) return null;

        var plan = await sceneDirector.CreateFullPlanAsync(sessionId, cancellationToken);
        pacingState = await LoadPacingStateAsync(session.CurrentBranchId, cancellationToken);
        var planVersion = new DirectorPlanVersion
        {
            BranchId = session.CurrentBranchId,
            SourceEventId = triggerEventId,
            PlanJson = JsonSerializer.Serialize(plan),
            ReflectionReason = trigger.Reason,
            ChangeSummary = "pending_story_thread_revision",
            PacePhase = pacingState?.Phase.ToString(),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O")
        };
        dbContext.DirectorPlanVersions.Add(planVersion);
        await dbContext.SaveChangesAsync(cancellationToken);
        var (changedThreadId, summary) = await ApplyStoryThreadBudgetAsync(
            trigger, triggerEventId, pacingState, planVersion.VersionId, session.WorldClockAnchor, cancellationToken);
        planVersion.ChangedStoryThreadId = changedThreadId;
        planVersion.ChangeSummary = summary;
        await dbContext.SaveChangesAsync(cancellationToken);
        await eventWriter.CommitAsync(
            sessionId,
            "DirectorReflection",
            JsonSerializer.Serialize(new
            {
                triggerEventId,
                triggerType,
                triggerReason = trigger.Reason,
                priority = trigger.Priority.ToString(),
                changedStoryThreadId = changedThreadId,
                changeSummary = summary,
                revisionBudget = trigger.Priority == ReflectionPriority.High ? 2 : 1,
                pacePhase = pacingState?.Phase.ToString()
            }),
            triggerCause: "director_reflection",
            cancellationToken: cancellationToken);
        return plan;
    }

    public static ReflectionTrigger EvaluateTrigger(
        string eventType,
        bool hasPlanVersion,
        PacingState? pacingState,
        string? lastReflectedPhase,
        int activeStoryThreadCount,
        bool revealQueueHasCausalConflict)
    {
        if (!hasPlanVersion) return new(true, "first_plan", ReflectionPriority.High);
        if (eventType is "PlayerDirectionRealizing" or "WorldRewindCommitted" or "InitialProjection")
            return new(true, eventType, ReflectionPriority.High);
        if (pacingState is not null && !string.Equals(lastReflectedPhase, pacingState.Phase.ToString(), StringComparison.Ordinal))
            return new(true, $"pace_phase_changed:{lastReflectedPhase ?? "none"}->{pacingState.Phase}", ReflectionPriority.Medium);
        if (pacingState is { BreathingDebt: >= 1.5 } or { ProgressDebt: >= 1.5 })
            return new(true, "pacing_debt_overflow", ReflectionPriority.Medium);
        if (revealQueueHasCausalConflict) return new(true, "reveal_causal_conflict", ReflectionPriority.Medium);
        if (activeStoryThreadCount == 0) return new(true, "story_thread_exhausted", ReflectionPriority.Medium);
        return new(false, "no_reflection_trigger", ReflectionPriority.None);
    }

    private async Task<PacingState?> LoadPacingStateAsync(string branchId, CancellationToken cancellationToken)
    {
        var json = await dbContext.PacingStateCache.AsNoTracking().Where(item => item.BranchId == branchId)
            .Select(item => item.StateJson).FirstOrDefaultAsync(cancellationToken);
        try { return json is null ? null : JsonSerializer.Deserialize<PacingState>(json); }
        catch (JsonException) { return null; }
    }

    private async Task<bool> HasRevealConflictAsync(int sessionId, string? worldClock, CancellationToken cancellationToken)
    {
        var deadlines = await dbContext.RevealQueue.AsNoTracking().Where(item => item.SessionId == sessionId
                && item.Status == "Pending" && item.MustReveal == 1)
            .Select(item => item.LatestRevealWorldTime).ToListAsync(cancellationToken);
        var now = DateTimeOffset.TryParse(worldClock, out var logical) ? logical : DateTimeOffset.UtcNow;
        return deadlines.Any(value => DateTimeOffset.TryParse(value, out var deadline) && deadline < now);
    }

    private async Task<(int? ChangedThreadId, string Summary)> ApplyStoryThreadBudgetAsync(
        ReflectionTrigger trigger,
        string triggerEventId,
        PacingState? pacing,
        int planVersionId,
        string? worldClock,
        CancellationToken cancellationToken)
    {
        var budget = trigger.Priority == ReflectionPriority.High ? 2 : 1;
        if (budget == 0) return (null, "no_story_thread_change");
        var active = await dbContext.StoryThreads.Where(item => item.Status == "Active")
            .OrderByDescending(item => item.Urgency).ThenBy(item => item.ThreadId).Take(budget).ToListAsync(cancellationToken);
        if (active.Count == 0)
        {
            var thread = new StoryThread
            {
                Scope = $"event:{triggerEventId}",
                Status = "Active",
                Urgency = pacing?.ProgressDebt >= 1 ? .65 : .4,
                Prerequisites = JsonSerializer.Serialize(new[] { $"event_committed:{triggerEventId}" }),
                UpdatedWorldTime = worldClock ?? DateTimeOffset.UtcNow.ToString("O"),
                LastPlanVersionId = planVersionId,
                ModifiedCount = 1
            };
            dbContext.StoryThreads.Add(thread);
            await dbContext.SaveChangesAsync(cancellationToken);
            return (thread.ThreadId, "created_story_thread_from_committed_trigger");
        }
        foreach (var thread in active)
        {
            var delta = pacing?.BreathingDebt >= 1 ? -.1 : pacing?.ProgressDebt >= 1 ? .15 : .05;
            thread.Urgency = Math.Clamp(thread.Urgency + delta, 0, 1);
            thread.UpdatedWorldTime = worldClock ?? DateTimeOffset.UtcNow.ToString("O");
            thread.LastPlanVersionId = planVersionId;
            thread.ModifiedCount++;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return (active[0].ThreadId, $"revised_{active.Count}_story_thread(s)_within_budget_{budget}");
    }
}

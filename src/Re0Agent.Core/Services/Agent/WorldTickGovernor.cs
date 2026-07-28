using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Database;

namespace Re0Agent.Core.Services.Agent;

public sealed record WorldTickResult(
    WorldClockAdvance Clock,
    string? ScheduledCharacterId,
    string? CommittedEventId,
    string? Error);

public sealed class WorldTickGovernor(
    Re0AgentDbContext dbContext,
    WorldClockService worldClockService,
    AgentOrchestrator agentOrchestrator,
    EventSingleWriter eventWriter,
    ObservabilityComputer observabilityComputer,
    SceneDirector sceneDirector,
    KeplerAgent keplerAgent,
    DirectionService directionService,
    OffscreenSimulationService offscreenSimulation,
    DirectorReflectionService directorReflection,
    WorldCandidateBuilder candidateBuilder,
    WorldCancellationRegistry cancellationRegistry)
{
    private const int MaxForegroundActionsPerWindow = 3;
    private static readonly TimeSpan SceneActionDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan BudgetWindow = TimeSpan.FromMinutes(1);

    public async Task<WorldTickResult> TickAsync(
        int sessionId,
        TimeSpan monotonicRealDelta,
        CancellationToken cancellationToken = default)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, cancellationRegistry.BackgroundToken(sessionId));
        cancellationToken = linkedCancellation.Token;
        var clock = await worldClockService.AdvanceAsync(sessionId, monotonicRealDelta, cancellationToken);
        if (clock.IsPaused || clock.WorldDelta <= TimeSpan.Zero)
        {
            return new WorldTickResult(clock, null, null, null);
        }

        var session = await dbContext.ChatSessions.SingleAsync(item => item.SessionId == sessionId, cancellationToken);
        if (session.IsActive != 1)
        {
            return new WorldTickResult(clock, null, null, null);
        }
        var pendingCount = await dbContext.WorldSchedulerJobs.CountAsync(
            item => item.SessionId == sessionId && item.Status == "Pending", cancellationToken);
        var oldestPending = await dbContext.WorldSchedulerJobs.Where(item => item.SessionId == sessionId && item.Status == "Pending")
            .OrderBy(item => item.ScheduledWorldTime).Select(item => item.ScheduledWorldTime).FirstOrDefaultAsync(cancellationToken);
        var lag = DateTimeOffset.TryParse(oldestPending, out var pendingTime)
            ? Math.Max(0, (clock.LogicalWorldTime - pendingTime).TotalSeconds) : 0;
        await worldClockService.SetBackpressureAsync(sessionId, pendingCount, lag, cancellationToken);
        BeatPlan? reflectionPlan = null;
        if (session.GameMode == "Theater")
        {
            var direction = await directionService.RealizeNextAsync(sessionId, cancellationToken);
            if (direction is not null)
            {
                var trigger = await dbContext.TimelineEvents.Where(item => item.BranchId == session.CurrentBranchId && item.EventType == "PlayerDirectionRealizing")
                    .OrderByDescending(item => item.Sequence).FirstOrDefaultAsync(cancellationToken);
                if (trigger is not null) reflectionPlan = await directorReflection.ReflectIfTriggeredAsync(sessionId, trigger.EventId, trigger.EventType, cancellationToken);
            }
        }
        if (reflectionPlan is null)
        {
            var latestEvent = await dbContext.TimelineEvents.Where(item => item.BranchId == session.CurrentBranchId && item.Status == "Committed")
                .OrderByDescending(item => item.Sequence).FirstOrDefaultAsync(cancellationToken);
            if (latestEvent is not null)
                reflectionPlan = await directorReflection.ReflectIfTriggeredAsync(sessionId, latestEvent.EventId, latestEvent.EventType, cancellationToken);
        }
        await offscreenSimulation.AdvanceDueAsync(sessionId, clock.LogicalWorldTime, cancellationToken);
        var sceneId = await dbContext.GlobalStates.AsNoTracking().Select(item => item.CurrentLocation)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(sceneId))
        {
            return new WorldTickResult(clock, null, null, null);
        }

        var runtime = await dbContext.WorldRuntimeStates.SingleAsync(item => item.SessionId == sessionId, cancellationToken);
        ResetBudgetWindowIfNeeded(runtime);
        if (runtime.CurrentSceneBudgetUsed >= MaxForegroundActionsPerWindow)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return new WorldTickResult(clock, null, null, null);
        }

        var dueJob = await FindOrScheduleJobAsync(session, sceneId, clock.LogicalWorldTime, runtime, cancellationToken);
        if (dueJob is null)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return new WorldTickResult(clock, null, null, null);
        }

        var payload = JsonSerializer.Deserialize<ScheduledCharacterAction>(dueJob.Payload ?? "{}")
            ?? throw new InvalidOperationException("世界调度任务数据损坏。");
        dueJob.Status = "Running";
        await dbContext.SaveChangesAsync(cancellationToken);
        try
        {
            var observers = await observabilityComputer.ComputeAsync(sceneId, null, null, cancellationToken);
            var opportunity = await eventWriter.CommitAsync(
                sessionId,
                "SceneOpportunity",
                "{\"reason\":\"scheduled_character_agency\"}",
                sceneId: sceneId,
                observers: observers,
                triggerCause: "world_scheduler",
                cancellationToken: cancellationToken);
            var action = await agentOrchestrator.RunCharacterActionAsync(
                payload.CharacterId, opportunity, payload.Opportunity, cancellationToken);
            var beatPlan = reflectionPlan ?? await sceneDirector.CreatePulseAsync(sessionId, cancellationToken);
            await keplerAgent.RenderAsync(sessionId, beatPlan, cancellationToken);
            dueJob.Status = "Completed";
            runtime.CurrentSceneBudgetUsed++;
            runtime.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
            await dbContext.SaveChangesAsync(cancellationToken);
            return new WorldTickResult(clock, payload.CharacterId, action.EventId, null);
        }
        catch (Exception exception)
        {
            dueJob.Status = "Failed";
            runtime.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
            await dbContext.SaveChangesAsync(cancellationToken);
            return new WorldTickResult(clock, payload.CharacterId, null, exception.Message);
        }
    }

    private async Task<WorldSchedulerJob?> FindOrScheduleJobAsync(
        ChatSession session,
        string sceneId,
        DateTimeOffset logicalNow,
        WorldRuntimeState runtime,
        CancellationToken cancellationToken)
    {
        var pending = await dbContext.WorldSchedulerJobs
            .Where(item => item.SessionId == session.SessionId && item.Status == "Pending")
            .OrderBy(item => item.ScheduledWorldTime).ToListAsync(cancellationToken);
        var due = pending.FirstOrDefault(item => DateTimeOffset.TryParse(item.ScheduledWorldTime, out var time) && time <= logicalNow);
        if (due is not null)
        {
            return due;
        }
        if (pending.Count > 0)
        {
            return null;
        }

        var eligible = (await candidateBuilder.BuildCurrentSceneAsync(session, sceneId, logicalNow, cancellationToken))
            .OrderBy(item => item.CharacterId, StringComparer.Ordinal).ToList();
        if (eligible.Count == 0)
        {
            return null;
        }

        var selected = eligible[runtime.CurrentSceneBudgetUsed % eligible.Count];
        var job = new WorldSchedulerJob
        {
            SessionId = session.SessionId,
            JobType = "CharacterAgency",
            ScheduledWorldTime = selected.EarliestWorldTime.ToString("O"),
            Payload = JsonSerializer.Serialize(new ScheduledCharacterAction(selected.CharacterId, selected.Opportunity)),
            Status = "Pending",
            CreatedAt = DateTimeOffset.UtcNow.ToString("O")
        };
        dbContext.WorldSchedulerJobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken);
        return job;
    }

    private static void ResetBudgetWindowIfNeeded(WorldRuntimeState runtime)
    {
        if (!DateTimeOffset.TryParse(runtime.BudgetWindowStartedAt, out var startedAt)
            || DateTimeOffset.UtcNow - startedAt >= BudgetWindow)
        {
            runtime.BudgetWindowStartedAt = DateTimeOffset.UtcNow.ToString("O");
            runtime.CurrentSceneBudgetUsed = 0;
        }
    }

    private sealed record ScheduledCharacterAction(string CharacterId, string Opportunity);
}

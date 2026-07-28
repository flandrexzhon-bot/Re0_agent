using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Services.Database;

namespace Re0Agent.Core.Services.Agent;

public sealed record WorldClockAdvance(
    int SessionId,
    TimeSpan WorldDelta,
    DateTimeOffset LogicalWorldTime,
    double EffectiveTimeScale,
    double GenerationBackpressureFactor,
    bool IsPaused);

public sealed class WorldClockService(Re0AgentDbContext dbContext, EventSingleWriter eventWriter)
{
    public async Task SetTimeScaleAsync(int sessionId, double timeScale, CancellationToken cancellationToken = default)
    {
        if (timeScale is < .1 or > 10) throw new ArgumentOutOfRangeException(nameof(timeScale));
        var session = await dbContext.ChatSessions.SingleAsync(item => item.SessionId == sessionId, cancellationToken);
        session.SessionTimeScale = timeScale;
        await dbContext.SaveChangesAsync(cancellationToken);
        await eventWriter.CommitAsync(
            sessionId,
            "RuntimeControl",
            $"{{\"control\":\"time_scale\",\"value\":{timeScale.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}",
            triggerCause: "runtime_control",
            cancellationToken: cancellationToken);
    }

    public async Task<WorldClockAdvance> AdvanceAsync(
        int sessionId,
        TimeSpan monotonicRealDelta,
        CancellationToken cancellationToken = default)
    {
        var session = await dbContext.ChatSessions.SingleAsync(item => item.SessionId == sessionId, cancellationToken);
        var runtime = await GetOrCreateRuntimeStateAsync(sessionId, cancellationToken);
        var current = ReadClock(session.WorldClockAnchor);
        if (session.IsPaused == 1)
        {
            return new WorldClockAdvance(sessionId, TimeSpan.Zero, current, 0, runtime.GenerationBackpressureFactor, true);
        }

        var effectiveScale = session.SessionTimeScale * session.InputSlowFactor * runtime.GenerationBackpressureFactor;
        var worldDelta = TimeSpan.FromTicks((long)(monotonicRealDelta.Ticks * effectiveScale));
        var next = current + worldDelta;
        session.WorldClockAnchor = next.ToString("O");
        runtime.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
        await dbContext.SaveChangesAsync(cancellationToken);
        return new WorldClockAdvance(sessionId, worldDelta, next, effectiveScale, runtime.GenerationBackpressureFactor, false);
    }

    public async Task SetPausedAsync(int sessionId, bool isPaused, CancellationToken cancellationToken = default)
    {
        var session = await dbContext.ChatSessions.SingleAsync(item => item.SessionId == sessionId, cancellationToken);
        session.IsPaused = isPaused ? 1 : 0;
        await dbContext.SaveChangesAsync(cancellationToken);
        await eventWriter.CommitAsync(
            sessionId,
            "RuntimeControl",
            isPaused ? "{\"control\":\"pause\"}" : "{\"control\":\"resume\"}",
            triggerCause: "runtime_control",
            cancellationToken: cancellationToken);
    }

    public async Task SetInputActivityAsync(
        int sessionId,
        bool isActive,
        double activeSlowFactor,
        string controlEventType,
        CancellationToken cancellationToken = default)
    {
        if (activeSlowFactor is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(activeSlowFactor));
        }
        var session = await dbContext.ChatSessions.SingleAsync(item => item.SessionId == sessionId, cancellationToken);
        var runtime = await GetOrCreateRuntimeStateAsync(sessionId, cancellationToken);
        session.InputSlowFactor = isActive ? activeSlowFactor : 1.0;
        runtime.InputActivityStartedAt = isActive ? DateTimeOffset.UtcNow.ToString("O") : null;
        runtime.InputEventCount = 0;
        await dbContext.SaveChangesAsync(cancellationToken);
        await eventWriter.CommitAsync(
            sessionId,
            controlEventType,
            $"{{\"active\":{isActive.ToString().ToLowerInvariant()}}}",
            triggerCause: "input_activity",
            cancellationToken: cancellationToken);
    }

    public async Task SetBackpressureAsync(
        int sessionId,
        int foregroundPendingCount,
        double foregroundLagSeconds,
        CancellationToken cancellationToken = default)
    {
        var runtime = await GetOrCreateRuntimeStateAsync(sessionId, cancellationToken);
        runtime.ForegroundPendingCount = Math.Max(0, foregroundPendingCount);
        runtime.ForegroundLagSeconds = Math.Max(0, foregroundLagSeconds);
        runtime.GenerationBackpressureFactor = ComputeBackpressure(runtime.ForegroundPendingCount, runtime.ForegroundLagSeconds);
        runtime.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<WorldRuntimeState> GetOrCreateRuntimeStateAsync(int sessionId, CancellationToken cancellationToken)
    {
        var state = await dbContext.WorldRuntimeStates.SingleOrDefaultAsync(item => item.SessionId == sessionId, cancellationToken);
        if (state is not null) return state;
        var now = DateTimeOffset.UtcNow.ToString("O");
        state = new WorldRuntimeState
        {
            SessionId = sessionId,
            BudgetWindowStartedAt = now,
            UpdatedAt = now
        };
        dbContext.WorldRuntimeStates.Add(state);
        await dbContext.SaveChangesAsync(cancellationToken);
        return state;
    }

    private static DateTimeOffset ReadClock(string? value) =>
        DateTimeOffset.TryParse(value, out var time) ? time : DateTimeOffset.UtcNow;

    private static double ComputeBackpressure(int pending, double lagSeconds) =>
        Math.Clamp(1d / (1d + pending * 0.15d + lagSeconds / 60d), 0.1d, 1d);
}

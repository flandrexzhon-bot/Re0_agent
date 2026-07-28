using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;

namespace Re0Agent.Core.Services.Agent;

public sealed class DirectorPulseScheduler(Re0AgentDbContext dbContext)
{
    private static readonly TimeSpan ResultLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumShadowWindow = TimeSpan.FromMinutes(30);

    public async Task EnqueueAfterCommitAsync(int sessionId, TimelineEvent trigger, CancellationToken cancellationToken)
    {
        if (!await ShouldTriggerAsync(sessionId, trigger, cancellationToken)) return;
        var pulseId = $"pulse:{trigger.EventId}";
        if (await dbContext.DirectorPulses.AnyAsync(item => item.PulseId == pulseId, cancellationToken)) return;
        var planVersion = await dbContext.DirectorPlanVersions.Where(item => item.BranchId == trigger.BranchId)
            .MaxAsync(item => (int?)item.VersionId, cancellationToken) ?? 0;
        var history = await dbContext.DirectorPulses.AsNoTracking().Where(item => item.SessionId == sessionId && item.BenefitScore != null)
            .OrderByDescending(item => item.CreatedAt).Take(12).ToListAsync(cancellationToken);
        var active = history.Count >= 10 && history.Average(item => item.BenefitScore!.Value) >= .65;
        if (!active && history.Count > 0 && DateTimeOffset.TryParse(history[^1].CreatedAt, out var started)
            && DateTimeOffset.UtcNow - started >= MaximumShadowWindow && !IsHighValue(trigger)) return;
        var now = DateTimeOffset.UtcNow;
        dbContext.DirectorPulses.Add(new DirectorPulse
        {
            PulseId = pulseId,
            SessionId = sessionId,
            TriggerEventId = trigger.EventId,
            PlanVersionId = planVersion,
            Status = "Pending",
            IsShadow = active ? 0 : 1,
            ExpiresAt = now.Add(ResultLifetime).ToString("O"),
            CreatedAt = now.ToString("O")
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> ShouldTriggerAsync(int sessionId, TimelineEvent trigger, CancellationToken cancellationToken)
    {
        if (trigger.EventType is "CharacterAction" or "KeplerNarration" or "RuleResolution" or "WorldRewindCommitted" or "PlayerDirectionRealizing")
            return true;
        if (trigger.EventType == "OffscreenFidelityPromoted") return true;
        if (trigger.EventType != "OffscreenRuleAdvance") return false;
        if (IsHighValue(trigger)) return true;
        return await dbContext.PendingDirections.AnyAsync(item => item.SessionId == sessionId
            && (item.Status == "Pending" || item.Status == "Realizing")
            && (item.Content.Contains(trigger.ActorId ?? "\u0000") || item.Content.Contains(trigger.SceneId ?? "\u0000")), cancellationToken);
    }

    private static bool IsHighValue(TimelineEvent trigger)
    {
        if (!string.IsNullOrWhiteSpace(trigger.StateChangeSet) && trigger.EventType is "RuleResolution" or "OffscreenFidelityPromoted") return true;
        if (string.IsNullOrWhiteSpace(trigger.PacingMetadata)) return false;
        try
        {
            using var json = JsonDocument.Parse(trigger.PacingMetadata);
            return json.RootElement.TryGetProperty("importance", out var value) && value.TryGetDouble(out var importance) && importance >= .75;
        }
        catch (JsonException) { return false; }
    }
}

public sealed class DirectorPulseProcessor(Re0AgentDbContext dbContext, SceneDirector sceneDirector)
{
    public async Task<bool> ProcessNextAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        var candidateId = await dbContext.DirectorPulses.AsNoTracking()
            .Where(item => item.SessionId == sessionId && item.Status == "Pending")
            .OrderBy(item => item.CreatedAt).Select(item => item.PulseId).FirstOrDefaultAsync(cancellationToken);
        if (candidateId is null) return false;
        var claimed = await dbContext.DirectorPulses.Where(item => item.PulseId == candidateId && item.Status == "Pending")
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, "Running"), cancellationToken);
        if (claimed == 0) return true;
        var pulse = await dbContext.DirectorPulses.SingleAsync(item => item.PulseId == candidateId, cancellationToken);
        try
        {
            if (DateTimeOffset.TryParse(pulse.ExpiresAt, out var expires) && expires <= DateTimeOffset.UtcNow)
            {
                pulse.Status = "Stale";
            }
            else
            {
                var branchId = await dbContext.TimelineEvents.AsNoTracking().Where(item => item.EventId == pulse.TriggerEventId)
                    .Select(item => item.BranchId).FirstOrDefaultAsync(cancellationToken);
                if (branchId is null)
                {
                    pulse.Status = "Stale";
                    return true;
                }
                var currentPlanVersion = await dbContext.DirectorPlanVersions.Where(item => item.BranchId == branchId)
                    .MaxAsync(item => (int?)item.VersionId, cancellationToken) ?? 0;
                if (currentPlanVersion != pulse.PlanVersionId)
                {
                    pulse.Status = "Stale";
                }
                else
                {
                    var baseline = await sceneDirector.CreateDeterministicPlanAsync(sessionId, cancellationToken);
                    var suggestion = await sceneDirector.CreatePulseAsync(sessionId, cancellationToken);
                    pulse.BaselineJson = JsonSerializer.Serialize(baseline);
                    pulse.SuggestionJson = JsonSerializer.Serialize(suggestion);
                    pulse.BenefitScore = Score(suggestion, baseline);
                    pulse.CompletedAt = DateTimeOffset.UtcNow.ToString("O");
                    pulse.Status = pulse.IsShadow == 1 ? "Shadowed" : "Completed";
                }
            }
        }
        catch (OperationCanceledException)
        {
            pulse.Status = "Pending";
            throw;
        }
        catch (Exception exception)
        {
            pulse.Status = "Failed";
            pulse.Error = exception.Message;
        }
        finally
        {
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
        return true;
    }

    private static double Score(BeatPlan suggestion, BeatPlan baseline)
    {
        var score = suggestion.CandidateEventIds.Count > 0 ? .25 : 0;
        if (!suggestion.CandidateEventIds.SequenceEqual(baseline.CandidateEventIds)) score += .25;
        if (!suggestion.Rationale.Contains("timeout_or_invalid", StringComparison.Ordinal)) score += .25;
        if (suggestion.RequestedIntensity <= baseline.RequestedIntensity + .3) score += .25;
        return score;
    }
}

public sealed class DirectorPulseCoordinator(IServiceScopeFactory scopeFactory, WorldCancellationRegistry cancellationRegistry)
{
    private readonly ConcurrentDictionary<int, byte> running = new();

    public void Kick(int sessionId)
    {
        if (!running.TryAdd(sessionId, 0)) return;
        var planToken = cancellationRegistry.PlanToken(sessionId);
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<DirectorPulseProcessor>();
                while (await processor.ProcessNextAsync(sessionId, planToken)) { }
            }
            catch (OperationCanceledException) when (planToken.IsCancellationRequested) { }
            finally
            {
                running.TryRemove(sessionId, out _);
            }
        });
    }
}

public sealed class DirectorPulseConsumer(Re0AgentDbContext dbContext)
{
    public async Task<BeatPlan?> ConsumeReadyAsync(int sessionId, int currentPlanVersion, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await dbContext.DirectorPulses.AsNoTracking().Where(item => item.SessionId == sessionId && item.Status == "Completed")
            .OrderByDescending(item => item.CompletedAt).ToListAsync(cancellationToken);
        var valid = rows.FirstOrDefault(item => item.PlanVersionId == currentPlanVersion
            && DateTimeOffset.TryParse(item.ExpiresAt, out var expires) && expires > now
            && item.SuggestionJson is not null);
        var staleIds = rows.Where(item => item.PulseId != valid?.PulseId).Select(item => item.PulseId).ToList();
        if (staleIds.Count > 0)
            await dbContext.DirectorPulses.Where(item => staleIds.Contains(item.PulseId) && item.Status == "Completed")
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, "Stale"), cancellationToken);
        if (valid is null) return null;
        var consumedAt = now.ToString("O");
        var claimed = await dbContext.DirectorPulses.Where(item => item.PulseId == valid.PulseId && item.Status == "Completed")
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, "Consumed")
                .SetProperty(item => item.ConsumedAt, consumedAt), cancellationToken);
        return claimed == 0 ? null : JsonSerializer.Deserialize<BeatPlan>(valid.SuggestionJson!);
    }
}

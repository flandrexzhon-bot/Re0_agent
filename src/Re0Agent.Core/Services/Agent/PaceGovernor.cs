using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;

namespace Re0Agent.Core.Services.Agent;

public sealed class PaceGovernor(Re0AgentDbContext dbContext, PacingFeatureExtractor extractor)
{
    public async Task<PacingState> ExtractAsync(string branchId, IReadOnlyList<TimelineEvent> recentEvents, CancellationToken cancellationToken = default)
    {
        var features = extractor.Extract(recentEvents);
        var cache = await dbContext.PacingStateCache.SingleOrDefaultAsync(item => item.BranchId == branchId, cancellationToken);
        var previous = cache is null ? null : JsonSerializer.Deserialize<PacingState>(cache.StateJson);
        var phase = NextPhase(previous?.Phase ?? PacePhase.Relax, previous?.PhaseAge ?? 0, features.Tension);
        var phaseAge = previous?.Phase == phase ? previous.PhaseAge + 1 : 1;
        var breathingDebt = Math.Max(0, (previous?.BreathingDebt ?? 0) + (features.Tension > .7 ? .4 : -.25));
        var progressDebt = Math.Max(0, (previous?.ProgressDebt ?? 0) + (features.ConsequenceDensity < .2 ? .35 : -.3));
        var state = new PacingState(
            features.Tension,
            features.Tension switch { < .3 => "Low", < .7 => "Medium", _ => "High" },
            features.InformationDensity,
            features.ConsequenceDensity,
            await dbContext.StoryThreads.CountAsync(item => item.Status == "Active", cancellationToken),
            breathingDebt,
            progressDebt,
            features.CameraAge,
            features.SpeakerRun,
            features.PlayerLoad,
            phase,
            phaseAge);
        if (recentEvents.Count > 0)
        {
            cache ??= new PacingStateCache { BranchId = branchId, EventId = recentEvents[^1].EventId, StateJson = "{}", UpdatedAt = "" };
            cache.EventId = recentEvents[^1].EventId;
            cache.StateJson = JsonSerializer.Serialize(state);
            cache.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
            if (dbContext.Entry(cache).State == EntityState.Detached) dbContext.PacingStateCache.Add(cache);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        return state;
    }

    public PaceDecision Decide(PacingState state) => state switch
    {
        { BreathingDebt: >= 1 } => new(.35, true, false, "呼吸债务达到阈值。"),
        { ProgressDebt: >= 1 } => new(.7, false, true, "推进债务达到阈值。"),
        { SpeakerRun: >= 3 } => new(.55, false, false, "同角色连续发言达到上限。"),
        { Phase: PacePhase.Fade } => new(.5, true, false, "Fade 阶段限制强度。"),
        { Phase: PacePhase.Relax } => new(.4, false, false, "Relax 阶段保持低强度。"),
        _ => new(1, false, false, "节奏在允许范围内。")
    };

    private static PacePhase NextPhase(PacePhase current, int age, double tension)
    {
        if (age < 2) return current;
        return current switch
        {
            PacePhase.Relax when tension >= .3 => PacePhase.Build,
            PacePhase.Build when tension >= .65 => PacePhase.Sustain,
            PacePhase.Sustain when tension <= .55 => PacePhase.Fade,
            PacePhase.Fade when tension <= .3 => PacePhase.Relax,
            PacePhase.Fade when tension >= .7 => PacePhase.Sustain,
            _ => current
        };
    }
}

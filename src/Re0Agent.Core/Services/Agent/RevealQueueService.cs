using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Services.Database;

namespace Re0Agent.Core.Services.Agent;

public sealed class RevealQueueService(Re0AgentDbContext dbContext, EventSingleWriter eventWriter)
{
    public async Task EnqueueAsync(int sessionId, string eventId, CancellationToken cancellationToken = default)
    {
        var exists = await dbContext.RevealQueue.AnyAsync(item => item.SessionId == sessionId && item.EventId == eventId, cancellationToken);
        if (!exists)
        {
            var eventRecord = await dbContext.TimelineEvents.AsNoTracking().SingleAsync(item => item.EventId == eventId, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            dbContext.RevealQueue.Add(new RevealQueueItem
            {
                SessionId = sessionId,
                EventId = eventId,
                Status = "Pending",
                OccurredWorldTime = eventRecord.WorldTime,
                Importance = ReadImportance(eventRecord.PacingMetadata),
                AllowedVisibilityScope = eventRecord.VisibilityScope,
                CausalDistance = 0,
                LatestRevealWorldTime = now.AddMinutes(5).ToString("O"),
                MustReveal = 1,
                CreatedAt = now.ToString("O")
            });
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<RevealQueueItem>> RevealReadyAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        var items = await dbContext.RevealQueue.Where(item => item.SessionId == sessionId && item.Status == "Pending")
            .OrderBy(item => item.QueueId).ToListAsync(cancellationToken);
        if (items.Count == 0) return items;
        await eventWriter.CommitAsync(
            sessionId,
            "RevealCommitted",
            "{}",
            triggerCause: "reveal_queue",
            revealedEventCursors: items.Select(item => item.EventId).ToList(),
            cancellationToken: cancellationToken);
        foreach (var item in items) item.Status = "Revealed";
        await dbContext.SaveChangesAsync(cancellationToken);
        return items;
    }

    private static double ReadImportance(string? pacingMetadata)
    {
        if (string.IsNullOrWhiteSpace(pacingMetadata)) return .5;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(pacingMetadata);
            return json.RootElement.TryGetProperty("importance", out var value) && value.TryGetDouble(out var result) ? Math.Clamp(result, 0, 1) : .5;
        }
        catch (System.Text.Json.JsonException) { return .5; }
    }
}

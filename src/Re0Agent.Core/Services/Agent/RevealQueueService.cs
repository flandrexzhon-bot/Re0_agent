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
            var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
            var protagonistId = protagonist is null ? null : $"protagonist:{protagonist.RowId}";
            var directObservers = System.Text.Json.JsonSerializer.Deserialize<string[]>(eventRecord.DirectObservers) ?? [];
            var mustReveal = protagonistId is not null && eventRecord.SceneId == protagonist?.LocationName && directObservers.Contains(protagonistId, StringComparer.Ordinal);
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
                MustReveal = mustReveal ? 1 : 0,
                CreatedAt = now.ToString("O")
            });
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<RevealQueueItem>> RevealReadyAsync(int sessionId, string? sceneId = null, CancellationToken cancellationToken = default)
    {
        var items = await (from queue in dbContext.RevealQueue
            join eventRecord in dbContext.TimelineEvents on queue.EventId equals eventRecord.EventId
            where queue.SessionId == sessionId && queue.Status == "Pending" && queue.MustReveal == 1
                && (sceneId == null || eventRecord.SceneId == sceneId)
            select queue).OrderBy(item => item.QueueId).ToListAsync(cancellationToken);
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

    public async Task<IReadOnlyList<RevealQueueItem>> RevealMustRevealForSceneAsync(
        int sessionId,
        string sceneId,
        DateTimeOffset worldTime,
        CancellationToken cancellationToken = default)
    {
        var candidates = await (from queue in dbContext.RevealQueue
            join eventRecord in dbContext.TimelineEvents on queue.EventId equals eventRecord.EventId
            where queue.SessionId == sessionId && queue.Status == "Pending" && queue.MustReveal == 1
                && eventRecord.SceneId == sceneId
            select queue).OrderBy(item => item.QueueId).ToListAsync(cancellationToken);
        var items = candidates.Where(item => !DateTimeOffset.TryParse(item.LatestRevealWorldTime, out var deadline) || deadline <= worldTime || item.MustReveal == 1).ToList();
        if (items.Count == 0) return items;
        await eventWriter.CommitAsync(sessionId, "RevealCommitted", "{}", triggerCause: "must_reveal_foreground", revealedEventCursors: items.Select(item => item.EventId).ToList(), cancellationToken: cancellationToken);
        foreach (var item in items) item.Status = "Revealed";
        await dbContext.SaveChangesAsync(cancellationToken);
        return items;
    }

    public async Task RebuildStatusesFromEventsAsync(int sessionId, CancellationToken cancellationToken = default)
    {
        var revealedIds = new HashSet<string>(StringComparer.Ordinal);
        var events = await (from eventRecord in dbContext.TimelineEvents
            join branch in dbContext.TimelineBranches on eventRecord.BranchId equals branch.BranchId
            join session in dbContext.ChatSessions on branch.SessionId equals session.SessionId
            where session.SessionId == sessionId
            select eventRecord.RevealedEventCursors).ToListAsync(cancellationToken);
        foreach (var cursorJson in events)
        {
            try
            {
                foreach (var id in System.Text.Json.JsonSerializer.Deserialize<string[]>(cursorJson) ?? []) revealedIds.Add(id);
            }
            catch (System.Text.Json.JsonException) { }
        }
        var queue = await dbContext.RevealQueue.Where(item => item.SessionId == sessionId).ToListAsync(cancellationToken);
        foreach (var item in queue) item.Status = revealedIds.Contains(item.EventId) ? "Revealed" : "Pending";
        await dbContext.SaveChangesAsync(cancellationToken);
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

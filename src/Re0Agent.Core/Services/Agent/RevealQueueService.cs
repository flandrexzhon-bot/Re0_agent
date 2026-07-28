using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Services.Database;
using System.Text.Json;

namespace Re0Agent.Core.Services.Agent;

public sealed class RevealQueueProjector(Re0AgentDbContext dbContext)
{
    private const int MaxPendingPerSession = 128;
    private const int MaxPendingPerStoryThread = 24;

    public async Task EnqueueAfterCommitAsync(int sessionId, TimelineEvent eventRecord, CancellationToken cancellationToken)
    {
        if (eventRecord.EventType is not ("CharacterAction" or "RuleResolution" or "KeplerNarration"
            or "OffscreenRuleAdvance" or "DistantWorldAdvance" or "OffscreenFidelityPromoted")) return;
        if (await dbContext.RevealQueue.AnyAsync(item => item.SessionId == sessionId && item.EventId == eventRecord.EventId, cancellationToken)) return;
        var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var protagonistId = protagonist is null ? null : $"protagonist:{protagonist.RowId}";
        var directObservers = DeserializeIds(eventRecord.DirectObservers);
        var mustReveal = protagonistId is not null && eventRecord.SceneId == protagonist?.LocationName
            && directObservers.Contains(protagonistId, StringComparer.Ordinal);
        var occurred = DateTimeOffset.TryParse(eventRecord.WorldTime, out var parsed) ? parsed : DateTimeOffset.UtcNow;
        dbContext.RevealQueue.Add(new RevealQueueItem
        {
            SessionId = sessionId,
            EventId = eventRecord.EventId,
            Status = "Pending",
            OccurredWorldTime = eventRecord.WorldTime,
            Importance = ReadImportance(eventRecord.PacingMetadata),
            AllowedVisibilityScope = eventRecord.VisibilityScope,
            CausalDistance = 0,
            LatestRevealWorldTime = occurred.AddMinutes(5).ToString("O"),
            MustReveal = mustReveal ? 1 : 0,
            CoalescedEventIds = "[]",
            MergeCategory = MergeCategory(eventRecord.EventType),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O")
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        await EnforceCapacityAsync(sessionId, cancellationToken);
    }

    private async Task EnforceCapacityAsync(int sessionId, CancellationToken cancellationToken)
    {
        var pending = await dbContext.RevealQueue.Where(item => item.SessionId == sessionId && item.Status == "Pending")
            .OrderBy(item => item.Importance).ThenBy(item => item.QueueId).ToListAsync(cancellationToken);
        var merge = new HashSet<int>();
        foreach (var group in pending.Where(item => item.StoryThreadId is not null).GroupBy(item => item.StoryThreadId))
            foreach (var item in group.Take(Math.Max(0, group.Count() - MaxPendingPerStoryThread)))
                if (item.MustReveal == 0) merge.Add(item.QueueId);
        foreach (var item in pending.Take(Math.Max(0, pending.Count - MaxPendingPerSession)))
            if (item.MustReveal == 0) merge.Add(item.QueueId);
        foreach (var item in pending.Where(item => merge.Contains(item.QueueId)))
        {
            var representative = pending.Where(candidate => !merge.Contains(candidate.QueueId) && candidate.MustReveal == 0
                    && candidate.AllowedVisibilityScope == item.AllowedVisibilityScope && candidate.StoryThreadId == item.StoryThreadId
                    && candidate.MergeCategory == item.MergeCategory)
                .OrderByDescending(candidate => candidate.Importance).ThenBy(candidate => candidate.QueueId).FirstOrDefault();
            if (representative is null) continue;
            representative.CoalescedEventIds = JsonSerializer.Serialize(
                DeserializeIds(representative.CoalescedEventIds).Append(item.EventId)
                    .Concat(DeserializeIds(item.CoalescedEventIds)).Distinct(StringComparer.Ordinal));
            item.Status = "Coalesced";
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    internal static IReadOnlyList<string> DeserializeIds(string value)
    {
        try { return JsonSerializer.Deserialize<string[]>(value) ?? []; }
        catch (JsonException) { return []; }
    }

    internal static double ReadImportance(string? pacingMetadata)
    {
        if (string.IsNullOrWhiteSpace(pacingMetadata)) return .5;
        try
        {
            using var json = JsonDocument.Parse(pacingMetadata);
            return json.RootElement.TryGetProperty("importance", out var value) && value.TryGetDouble(out var result) ? Math.Clamp(result, 0, 1) : .5;
        }
        catch (JsonException) { return .5; }
    }

    internal static string MergeCategory(string eventType) => eventType switch
    {
        "CharacterAction" or "KeplerNarration" => "dialogue_narration",
        "RuleResolution" => "rule_consequence",
        "OffscreenRuleAdvance" or "DistantWorldAdvance" or "OffscreenFidelityPromoted" => "offscreen_world",
        _ => "other"
    };
}

public sealed class RevealQueueService(Re0AgentDbContext dbContext, EventSingleWriter eventWriter)
{
    private const int MaxPendingPerSession = 128;
    private const int MaxPendingPerStoryThread = 24;

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
            var occurred = DateTimeOffset.TryParse(eventRecord.WorldTime, out var parsed) ? parsed : now;
            dbContext.RevealQueue.Add(new RevealQueueItem
            {
                SessionId = sessionId,
                EventId = eventId,
                Status = "Pending",
                OccurredWorldTime = eventRecord.WorldTime,
                Importance = RevealQueueProjector.ReadImportance(eventRecord.PacingMetadata),
                AllowedVisibilityScope = eventRecord.VisibilityScope,
                CausalDistance = 0,
                LatestRevealWorldTime = occurred.AddMinutes(5).ToString("O"),
                MustReveal = mustReveal ? 1 : 0,
                CoalescedEventIds = "[]",
                MergeCategory = RevealQueueProjector.MergeCategory(eventRecord.EventType),
                CreatedAt = now.ToString("O")
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            await EnforceCapacityAsync(sessionId, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<RevealQueueItem>> RevealReadyAsync(int sessionId, string? sceneId = null, CancellationToken cancellationToken = default)
    {
        var items = await (from queue in dbContext.RevealQueue
            join eventRecord in dbContext.TimelineEvents on queue.EventId equals eventRecord.EventId
            where queue.SessionId == sessionId && queue.Status == "Pending"
                && (sceneId == null || eventRecord.SceneId == sceneId)
            select queue).OrderByDescending(item => item.MustReveal).ThenByDescending(item => item.Importance)
            .ThenBy(item => item.QueueId).Take(8).ToListAsync(cancellationToken);
        if (items.Count == 0) return items;
        var cursors = ExpandCursors(items);
        await eventWriter.CommitAsync(
            sessionId,
            "RevealCommitted",
            "{}",
            triggerCause: "reveal_queue",
            revealedEventCursors: cursors,
            cancellationToken: cancellationToken);
        await MarkRevealedAsync(sessionId, cursors, cancellationToken);
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
        var cursors = ExpandCursors(items);
        await eventWriter.CommitAsync(sessionId, "RevealCommitted", "{}", triggerCause: "must_reveal_foreground", revealedEventCursors: cursors, cancellationToken: cancellationToken);
        await MarkRevealedAsync(sessionId, cursors, cancellationToken);
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
        var coalescedIds = queue.SelectMany(item => DeserializeIds(item.CoalescedEventIds)).ToHashSet(StringComparer.Ordinal);
        foreach (var item in queue)
            item.Status = revealedIds.Contains(item.EventId) ? "Revealed" : coalescedIds.Contains(item.EventId) ? "Coalesced" : "Pending";
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task EnforceCapacityAsync(int sessionId, CancellationToken cancellationToken)
    {
        var pending = await dbContext.RevealQueue.Where(item => item.SessionId == sessionId && item.Status == "Pending")
            .OrderBy(item => item.Importance).ThenBy(item => item.QueueId).ToListAsync(cancellationToken);
        var merge = new HashSet<int>();
        foreach (var group in pending.Where(item => item.StoryThreadId is not null).GroupBy(item => item.StoryThreadId))
            foreach (var item in group.Take(Math.Max(0, group.Count() - MaxPendingPerStoryThread)))
                if (item.MustReveal == 0) merge.Add(item.QueueId);
        foreach (var item in pending.Take(Math.Max(0, pending.Count - MaxPendingPerSession)))
            if (item.MustReveal == 0) merge.Add(item.QueueId);
        foreach (var item in pending.Where(item => merge.Contains(item.QueueId)))
        {
            var representative = pending.Where(candidate => !merge.Contains(candidate.QueueId)
                    && candidate.MustReveal == 0
                    && candidate.AllowedVisibilityScope == item.AllowedVisibilityScope
                    && candidate.StoryThreadId == item.StoryThreadId
                    && candidate.MergeCategory == item.MergeCategory)
                .OrderByDescending(candidate => candidate.Importance).ThenBy(candidate => candidate.QueueId).FirstOrDefault();
            if (representative is null) continue;
            representative.CoalescedEventIds = JsonSerializer.Serialize(
                DeserializeIds(representative.CoalescedEventIds).Append(item.EventId)
                    .Concat(DeserializeIds(item.CoalescedEventIds)).Distinct(StringComparer.Ordinal));
            item.Status = "Coalesced";
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkRevealedAsync(int sessionId, IReadOnlyList<string> eventIds, CancellationToken cancellationToken)
    {
        var rows = await dbContext.RevealQueue.Where(item => item.SessionId == sessionId && eventIds.Contains(item.EventId))
            .ToListAsync(cancellationToken);
        foreach (var row in rows) row.Status = "Revealed";
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static IReadOnlyList<string> ExpandCursors(IEnumerable<RevealQueueItem> items) => items
        .SelectMany(item => new[] { item.EventId }.Concat(DeserializeIds(item.CoalescedEventIds)))
        .Distinct(StringComparer.Ordinal).ToList();

    private static IReadOnlyList<string> DeserializeIds(string value)
    {
        try { return JsonSerializer.Deserialize<string[]>(value) ?? []; }
        catch (JsonException) { return []; }
    }

}

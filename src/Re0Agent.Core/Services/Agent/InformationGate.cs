using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;

namespace Re0Agent.Core.Services.Agent;

public sealed class InformationGate(Re0AgentDbContext dbContext, MemoryRetrievalService memoryRetrieval)
{
    public async Task<ActorBrief> BuildActorBriefAsync(
        ChatSession session,
        string characterId,
        string contextEventId,
        string? opportunity,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(session.CurrentBranchId))
        {
            throw new InvalidOperationException("会话没有当前时间线分支。");
        }
        var candidates = await dbContext.TimelineEvents.AsNoTracking()
            .Where(item => item.BranchId == session.CurrentBranchId && item.Status == "Committed")
            .OrderByDescending(item => item.Sequence).Take(128).ToListAsync(cancellationToken);
        var revealed = candidates.Where(item => item.EventType == "RevealCommitted")
            .SelectMany(item => DeserializeIds(item.RevealedEventCursors)).ToHashSet(StringComparer.Ordinal);
        var isProtagonist = characterId.StartsWith("protagonist:", StringComparison.Ordinal);
        var visible = candidates.Where(item => IsDirectObserver(item, characterId) || isProtagonist && revealed.Contains(item.EventId))
            .OrderBy(item => item.Sequence).ToList();
        var context = visible.SingleOrDefault(item => item.EventId == contextEventId)
            ?? throw new InvalidOperationException("角色不能以未观察到的事件作为行动上下文。");
        var memories = await memoryRetrieval.RetrieveAsync(characterId, session.CurrentWorldEpoch, limit: 8, cancellationToken: cancellationToken);
        return new ActorBrief(characterId, context.SceneId, context, visible.TakeLast(12).ToList(), memories, opportunity);
    }

    private static bool IsDirectObserver(TimelineEvent eventRecord, string characterId)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(eventRecord.DirectObservers)?.Contains(characterId, StringComparer.Ordinal) == true;
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("事件的 direct_observers 数据损坏。");
        }
    }

    private static IReadOnlyList<string> DeserializeIds(string value)
    {
        try { return JsonSerializer.Deserialize<string[]>(value) ?? []; }
        catch (JsonException) { return []; }
    }
}

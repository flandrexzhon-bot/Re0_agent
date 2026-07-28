using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Database;
using Re0Agent.Core.Services.Llm;

namespace Re0Agent.Core.Services.Agent;

public sealed class KeplerAgent(
    Re0AgentDbContext dbContext,
    AgentConfigResolver configResolver,
    ILlmClient llmClient,
    EventSingleWriter eventWriter,
    ObservabilityComputer observabilityComputer,
    RevealQueueService revealQueueService)
{
    public async Task<string> RenderAsync(int sessionId, BeatPlan plan, CancellationToken cancellationToken = default)
    {
        var facts = await dbContext.TimelineEvents.AsNoTracking().Where(item => plan.CandidateEventIds.Contains(item.EventId))
            .OrderBy(item => item.Sequence).ToListAsync(cancellationToken);
        // 未迁移的 GM 配置是开普勒的提示词来源，保留用户既有文风与设定。
        var config = await configResolver.FindConfigAsync("Kepler", "开普勒", cancellationToken)
            ?? await configResolver.FindConfigAsync("GM", "GM", cancellationToken);
        var response = await llmClient.SendChatAsync(new LlmRequest
        {
            AgentName = "开普勒",
            Options = AgentConfigResolver.ToLlmOptions(config),
            Messages = [LlmMessage.System(config?.SystemPrompt ?? "你是开普勒，只描写已确定事实带来的环境、背景、镜头与转场；不替角色行动或发言。"),
                LlmMessage.User($"BeatPlan：{JsonSerializer.Serialize(plan)}\n已确定事实：{JsonSerializer.Serialize(facts.Select(f => new { f.EventType, f.Content, f.SceneId }))}")]
        }, cancellationToken);
        var sceneId = facts.LastOrDefault()?.SceneId;
        var observers = await observabilityComputer.ComputeAsync(sceneId, null, null, cancellationToken);
        var narration = await eventWriter.CommitAsync(sessionId, "KeplerNarration", response.Content, sceneId: sceneId, observers: observers, triggerCause: "kepler_render", causalParentEventId: facts.LastOrDefault()?.EventId, revealedEventCursors: facts.Select(item => item.EventId).ToList(), cancellationToken: cancellationToken);
        await revealQueueService.EnqueueAsync(sessionId, narration.EventId, cancellationToken);
        var session = await dbContext.ChatSessions.AsNoTracking().SingleAsync(item => item.SessionId == sessionId, cancellationToken);
        var protagonistId = await dbContext.ProtagonistInfo.AsNoTracking().Select(item => $"protagonist:{item.RowId}").FirstOrDefaultAsync(cancellationToken);
        if (session.GameMode == "Theater" || protagonistId is not null && observers.DirectObserverIds.Contains(protagonistId, StringComparer.Ordinal))
        {
            await revealQueueService.RevealReadyAsync(sessionId, sceneId, cancellationToken);
        }
        return response.Content;
    }
}

using System.Text.Json;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Database;
using Re0Agent.Core.Services.Llm;

namespace Re0Agent.Core.Services.Agent;

public sealed class FormAgent(
    AgentConfigResolver configResolver,
    ILlmClient llmClient,
    StateChangeSetValidator validator)
{
    public async Task<StateChangeSet> ProposeAsync(
        TimelineEvent eventRecord,
        string projectionSummary,
        CancellationToken cancellationToken = default)
    {
        var config = await configResolver.FindConfigAsync("Form", "填表Agent", cancellationToken);
        var response = await llmClient.SendChatAsync(
            new LlmRequest
            {
                AgentName = "填表Agent",
                Options = AgentConfigResolver.ToLlmOptions(config),
                Messages =
                [
                    LlmMessage.System(config?.SystemPrompt ?? "你只输出 StateChangeSet JSON，绝不输出 SQL。"),
                    LlmMessage.User(
                        $"根据已提交事件提出状态变更草案。不得补写事件文本中不存在的事实。\n"
                        + $"事件类型：{eventRecord.EventType}\n事件内容：{eventRecord.Content}\n当前投影：{projectionSummary}\n"
                        + "输出格式：{\"commands\":[{\"commandId\":\"稳定ID\",\"projection\":\"表名\",\"entityId\":\"实体ID\",\"operationType\":\"insert|update|delete\",\"expectedVersion\":null,\"fieldChanges\":{},\"validationResult\":\"proposed\"}]}")
                ]
            },
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(response.ErrorMessage))
        {
            throw new InvalidOperationException(response.ErrorMessage);
        }

        var proposal = Deserialize(response.Content);
        return validator.Validate(proposal);
    }

    private static StateChangeSet Deserialize(string content)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        if (!root.TryGetProperty("commands", out var commands))
        {
            throw new InvalidOperationException("填表 Agent 未返回 commands。");
        }

        return new StateChangeSet(JsonSerializer.Deserialize<List<StateChangeCommand>>(commands.GetRawText()) ?? []);
    }
}

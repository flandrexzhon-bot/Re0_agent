using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Re0Agent.Core.Services.Llm;

public sealed class FakeLlmClient : ILlmClient
{
    public Task<LlmResponse> SendChatAsync(
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new LlmResponse(request.AgentName, CreateFakeContent(request), UsedFakeClient: true));
    }

    public async IAsyncEnumerable<LlmStreamChunk> StreamChatAsync(
        LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = CreateFakeContent(request);

        foreach (var chunk in response.Chunk(24))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new LlmStreamChunk(request.AgentName, new string(chunk), IsDone: false, UsedFakeClient: true);
        }

        yield return new LlmStreamChunk(request.AgentName, string.Empty, IsDone: true, UsedFakeClient: true);
    }

    private static string CreateFakeContent(LlmRequest request)
    {
        var prompt = string.Join('\n', request.Messages.Select(message => message.Content));

        if (prompt.Contains("填表Agent", StringComparison.OrdinalIgnoreCase)
            || request.AgentName.Contains("Form", StringComparison.OrdinalIgnoreCase)
            || request.AgentName.Contains("填表", StringComparison.OrdinalIgnoreCase))
        {
            return CreateFakeSqlPayload();
        }

        if (prompt.Contains("位号", StringComparison.OrdinalIgnoreCase)
            || request.AgentName.Equals("GM", StringComparison.OrdinalIgnoreCase))
        {
            if (prompt.Contains("ChapterSwitchRequest", StringComparison.OrdinalIgnoreCase))
            {
                return "GM总结：章节转换中。\n<update>\n_.set('chapter', 2);\n</update>";
            }
            return "GM开场：当前场景稳定，角色按在场顺序行动。位号：1 爱蜜莉雅，2 菜月昴。判定：无。";
        }

        return $"{request.AgentName}行动：保持角色立场，观察当前局势并作出谨慎回应。";
    }

    private static string CreateFakeSqlPayload()
    {
        var chronicleText = string.Concat(Enumerable.Repeat(
            "本轮中，GM整理了当前场景，角色按照位号完成行动，主角作为角色Agent在最后回应。场景状态保持稳定，所有记录用于Phase2编排验证。",
            4));

        var payload = new
        {
            sql = new[]
            {
                """
                INSERT INTO chronicle (row_id, code_index, time_span, summary, chronicle_text)
                VALUES ((SELECT COALESCE(MAX(row_id), 0) + 1 FROM chronicle), 'AM' || printf('%04d', (SELECT COALESCE(MAX(CAST(SUBSTR(code_index, 3) AS INTEGER)), 0) + 1 FROM chronicle)), '2024-04-01 09:00 ~ 2024-04-01 09:10', 'Phase2回合演示', '__CHRONICLE__')
                """.Replace("__CHRONICLE__", chronicleText),
                """
                INSERT INTO character_memory (row_id, character_name, round_index, memory_text, emotional_state, created_at)
                VALUES ((SELECT COALESCE(MAX(row_id), 0) + 1 FROM character_memory), '菜月昴', 'R0001', '他记住了本轮自己作为角色Agent最后行动。', '专注', '2024-04-01 09:10')
                """
            }
        };

        return JsonSerializer.Serialize(payload);
    }
}

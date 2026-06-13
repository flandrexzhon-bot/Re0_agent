using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Llm;

namespace Re0Agent.Core.Services.Agent;

public sealed class FormAgent(
    Re0AgentDbContext dbContext,
    AgentConfigResolver configResolver,
    PromptComposer promptComposer,
    ILlmClient llmClient)
{
    public async Task<IReadOnlyList<string>> GenerateSqlAsync(
        GameRound round,
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
                    LlmMessage.System(config?.SystemPrompt ?? "你是填表Agent，只输出JSON包装的SQL数组。"),
                    LlmMessage.User(promptComposer.ComposeFormAgent(round, await CreateDatabaseSummaryAsync(cancellationToken)))
                ]
            },
            cancellationToken);

        return ParseSqlPayload(response.Content);
    }

    private async Task<string> CreateDatabaseSummaryAsync(CancellationToken cancellationToken)
    {
        var global = await dbContext.GlobalStates.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var npcNames = await dbContext.ImportantNpcs.AsNoTracking()
            .OrderBy(n => n.RowId)
            .Select(n => n.Name)
            .ToListAsync(cancellationToken);
        var memoryCount = await dbContext.CharacterMemory.CountAsync(cancellationToken);

        var npcList = npcNames.Count == 0 ? "（无）" : string.Join("、", npcNames);

        return $"global={(global is null ? "none" : $"{global.CurrentLocation}/{global.CurTime}/chapter={global.CurrentChapter}")}; protagonist={protagonist?.Name ?? "none"}; 已在册NPC({npcNames.Count}个)=[{npcList}]; memory_count={memoryCount}";
    }

    private static IReadOnlyList<string> ParseSqlPayload(string content)
    {
        using var document = JsonDocument.Parse(content);
        var sql = document.RootElement.GetProperty("sql");
        return sql.EnumerateArray()
            .Select(element => element.GetString())
            .Where(statement => !string.IsNullOrWhiteSpace(statement))
            .Select(statement => statement!)
            .ToList();
    }
}

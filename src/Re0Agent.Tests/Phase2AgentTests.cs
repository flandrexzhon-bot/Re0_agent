using Re0Agent.Core.Services.Database;
using Re0Agent.Core.Services.Llm;
using Re0Agent.Core.Services.Settings;

namespace Re0Agent.Tests;

public sealed class Phase2AgentTests
{
    [Fact]
    public async Task SseParserReadsContentDeltas()
    {
        const string payload = """
            data: {"choices":[{"delta":{"content":"第一段"}}]}

            data: {"choices":[{"delta":{"content":"第二段"}}]}

            data: [DONE]

            """;

        using var reader = new StringReader(payload);
        var deltas = new List<string>();

        await foreach (var delta in SseParser.ReadContentDeltasAsync(reader))
        {
            deltas.Add(delta);
        }

        Assert.Equal(["第一段", "第二段"], deltas);
    }

    [Fact]
    public void SqlSafetyValidatorRejectsUnsafeStatements()
    {
        var validator = new SqlSafetyValidator();

        Assert.True(validator.ValidateStatement("INSERT INTO chronicle (row_id, code_index, time_span, summary, chronicle_text) VALUES (1, 'AM0001', '2024-04-01 09:00 ~ 2024-04-01 09:10', '摘要', '正文')").IsValid);
        Assert.False(validator.ValidateStatement("DELETE FROM chronicle WHERE row_id = 1").IsValid);
        Assert.False(validator.ValidateStatement("DROP TABLE chronicle").IsValid);
        Assert.False(validator.ValidateStatement("INSERT INTO save_points (save_id) VALUES (1)").IsValid);
        Assert.False(validator.ValidateStatement("UPDATE chronicle SET summary = 'a'; UPDATE chronicle SET summary = 'b'").IsValid);
    }

    [Fact]
    public async Task RagServiceListsAllBuiltInEntries()
    {
        var ragService = new BlackTeaRagService(new BlackTeaImporter(), new ChapterVariantRenderer());

        var entries = await ragService.ListAllEntriesAsync();

        Assert.NotEmpty(entries);
    }
}

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

        // 字符串字面量内的分号是合法值的一部分（如属性串），不应被当成多语句。
        Assert.True(validator.ValidateStatement("UPDATE important_npc SET base_attributes = '体质:95; 敏捷:98; 感知:95; 意志:90' WHERE name = '罗兹瓦尔·L·梅瑟斯'").IsValid);
        Assert.True(validator.ValidateStatement("INSERT INTO important_npc (name, base_attributes) VALUES ('碧翠丝', '体质:40; 敏捷:60; 感知:99; 意志:80')").IsValid);
        // 末尾单个分号仍合法。
        Assert.True(validator.ValidateStatement("UPDATE important_npc SET self_status = '正常' WHERE name = '雷姆';").IsValid);
        // 字面量内的双连字符不应被误判为 SQL 注释。
        Assert.True(validator.ValidateStatement("UPDATE important_npc SET relations_text = '昴--信赖' WHERE name = '雷姆'").IsValid);
        // 新引入角色允许 INSERT OR IGNORE / INSERT OR REPLACE。
        Assert.True(validator.ValidateStatement("INSERT OR IGNORE INTO important_npc (name, gender) VALUES ('菲鲁特', '女')").IsValid);
        Assert.True(validator.ValidateStatement("INSERT OR REPLACE INTO important_npc (name, gender) VALUES ('菲鲁特', '女')").IsValid);
    }

    [Fact]
    public async Task RagServiceListsAllBuiltInEntries()
    {
        var ragService = new BlackTeaRagService(new ChapterVariantRenderer());

        var entries = await ragService.ListAllEntriesAsync();

        Assert.NotEmpty(entries);
    }
}

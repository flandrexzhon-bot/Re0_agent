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

        // DELETE：允许带 WHERE 删除白名单表，但禁止删除编年史 chronicle，禁止无条件删除。
        Assert.True(validator.ValidateStatement("DELETE FROM inventory WHERE quantity <= 0").IsValid);
        Assert.True(validator.ValidateStatement("DELETE FROM important_npc WHERE name = '路人'").IsValid);
        Assert.False(validator.ValidateStatement("DELETE FROM inventory").IsValid);
        Assert.False(validator.ValidateStatement("DELETE FROM save_points WHERE save_id = 1").IsValid);
    }

    [Fact]
    public async Task RagServiceListsAllBuiltInEntries()
    {
        var ragService = new BlackTeaRagService(new ChapterVariantRenderer());

        var entries = await ragService.ListAllEntriesAsync();

        Assert.NotEmpty(entries);
    }

    [Fact]
    public void RoundContextIncludesCurrentRoundEvenWithEmptyChronicle()
    {
        var round = new Re0Agent.Core.Models.GameRound
        {
            RoundIndex = "R1",
            Chapter = 1,
            GmOpening = "王都广场，菜月昴刚刚抵达。",
            CharacterTurns =
            {
                new Re0Agent.Core.Models.CharacterTurn
                {
                    RoundIndex = "R1", OrderNumber = 1, CharacterName = "菜月昴",
                    ActionText = "他四处张望，试图弄清状况。"
                }
            }
        };

        // 编年史为空（开局/并发未写入）时，历史仍应包含本回合原版上下文，而非"无历史"。
        var history = Re0Agent.Core.Services.Agent.RoundContextBuilder.BuildHistory(
            round, []);

        Assert.DoesNotContain("无历史记录", history);
        Assert.Contains("王都广场", history);
        Assert.Contains("菜月昴", history);
        Assert.Contains("他四处张望", history);
    }

    [Fact]
    public void RoundContextMergesChronicleSummariesAndCurrentRound()
    {
        var round = new Re0Agent.Core.Models.GameRound
        {
            RoundIndex = "R2",
            Chapter = 1,
            GmOpening = "次日清晨。"
        };
        var chronicle = new[]
        {
            new Re0Agent.Core.Entities.ChronicleEntry
            {
                CodeIndex = "AM0001", TimeSpan = "x ~ y",
                Summary = "抵达王都", ChronicleText = "菜月昴抵达王都并卷入事件。"
            }
        };

        var history = Re0Agent.Core.Services.Agent.RoundContextBuilder.BuildHistory(round, chronicle);

        Assert.Contains("AM0001", history);
        Assert.Contains("抵达王都", history);
        Assert.Contains("次日清晨", history);
    }

    [Fact]
    public void ChronicleSelectorAlwaysKeepsRecentFiveAndTopKeywordMatches()
    {
        // 构造 30 条编年史：RowId 1..30，升序。
        // 偶数条提到"雷姆"，其中 RowId=2/4/6 提及多次（命中数更高）。
        var all = new List<Re0Agent.Core.Entities.ChronicleEntry>();
        for (var i = 1; i <= 30; i++)
        {
            var text = i % 2 == 0 ? $"第{i}回合，雷姆出现了。" : $"第{i}回合，平淡无事。";
            if (i is 2 or 4 or 6)
            {
                text += "雷姆雷姆雷姆"; // 提高命中数
            }

            all.Add(new Re0Agent.Core.Entities.ChronicleEntry
            {
                RowId = i, CodeIndex = $"AM{i:0000}", TimeSpan = "x ~ y",
                Summary = "s", ChronicleText = text
            });
        }

        var selected = Re0Agent.Core.Services.Agent.ChronicleSelector.Select(all, ["雷姆"]);
        var ids = selected.Select(c => c.RowId).ToList();

        // 最近 5 条（26..30）必选。
        foreach (var id in new[] { 26, 27, 28, 29, 30 })
        {
            Assert.Contains(id, ids);
        }

        // 关键词命中最高的 2/4/6 应入选（不在最近5里，命中数最高）。
        Assert.Contains(2, ids);
        Assert.Contains(4, ids);
        Assert.Contains(6, ids);

        // 总数 = 5 最近 + 至多 15 关键词，且升序排列。
        Assert.True(selected.Count <= 20);
        Assert.Equal(ids.OrderBy(x => x), ids);

        // 关键词命中数为 0 的奇数条（不在最近5里），不会被关键词选中（如 RowId=1）。
        Assert.DoesNotContain(1, ids);
    }

    [Fact]
    public void ChronicleSelectorReturnsAllWhenFewerThanFive()
    {
        var all = Enumerable.Range(1, 3)
            .Select(i => new Re0Agent.Core.Entities.ChronicleEntry
            {
                RowId = i, CodeIndex = $"AM{i:0000}", TimeSpan = "x ~ y",
                Summary = "s", ChronicleText = $"第{i}回合。"
            })
            .ToList();

        var selected = Re0Agent.Core.Services.Agent.ChronicleSelector.Select(all, ["雷姆"]);

        Assert.Equal(3, selected.Count);
    }
}

using Re0Agent.Core.Database;
using Re0Agent.Core.Services.Settings;

namespace Re0Agent.Tests;

public sealed class JsonResourceTests
{
    [Fact]
    public void EmbeddedSqlSheetCatalogExposesOriginalDdl()
    {
        var globalData = SqlSheetCatalog.GetRequired("sheet_global_data");

        Assert.Contains("CREATE TABLE global_state", globalData.Ddl);
        Assert.Contains("current_location", globalData.Ddl);
        Assert.Contains("当前详细地点", globalData.Content[0]);
        Assert.Contains(SqlSheetCatalog.Sheets, sheet => sheet.Uid == "sheet_check_suggestions");
    }

    [Fact]
    public void EmbeddedBlackTeaWorldBookContainsEntries()
    {
        var entries = BlackTeaWorldBook.Entries;

        Assert.InRange(entries.Count, 220, 230);
        Assert.Contains(entries, entry => entry.Comment.Contains("基础", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Comment.StartsWith("第", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Comment.Contains("第54章", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, entry => entry.Comment is "变量提示词" or "[initvar]" or "🔆状态栏🔆");
        Assert.DoesNotContain(entries, entry => !entry.Enabled);
    }
}

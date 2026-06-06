using System.Text;
using System.Text.Json;

namespace Re0Agent.Tests;

public sealed class JsonResourceTests
{
    [Fact]
    public async Task SqlSheetJsonParsesAsUtf8AndExposesOriginalDdl()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root.FullName, "data", "settings", "骰子表格SQL_v4.1.json");
        var jsonText = await File.ReadAllTextAsync(path, Encoding.UTF8);

        using var document = JsonDocument.Parse(jsonText);
        var rootElement = document.RootElement;

        Assert.True(rootElement.TryGetProperty("sheet_global_data", out var globalData));
        var ddl = globalData.GetProperty("sourceData").GetProperty("ddl").GetString();

        Assert.Contains("CREATE TABLE global_state", ddl);
        Assert.True(rootElement.TryGetProperty("sheet_check_suggestions", out _));
    }

    [Fact]
    public async Task BlackTeaWorldBookJsonParsesAndContainsEntries()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root.FullName, "data", "settings", "REZero_BlackTea_v2.0.0.json");
        var jsonText = await File.ReadAllTextAsync(path, Encoding.UTF8);

        using var document = JsonDocument.Parse(jsonText);
        var entries = document.RootElement
            .GetProperty("originalData")
            .GetProperty("entries");

        Assert.InRange(entries.GetArrayLength(), 200, 220);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Re0Agent.sln")))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new DirectoryNotFoundException("Could not locate Re0Agent.sln.");
    }
}

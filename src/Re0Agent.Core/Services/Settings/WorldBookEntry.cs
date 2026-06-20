namespace Re0Agent.Core.Services.Settings;

public sealed class WorldBookEntry
{
    public IReadOnlyList<string> Keys { get; init; } = [];
    public IReadOnlyList<string> SecondaryKeys { get; init; } = [];
    public string Comment { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public bool Constant { get; init; }
    public bool Selective { get; init; }
    public int InsertionOrder { get; init; }
    public bool Enabled { get; init; }
    public string? Position { get; init; }
    public bool UseRegex { get; init; }

    /// <summary>
    /// 预制角色的稳定身份ID（与出场位号无关）。仅角色条目有意义，0 表示非角色或未分配。
    /// 骰子/战斗/落库按此 ID 认人，取代脆弱的别名匹配。
    /// </summary>
    public int CharId { get; init; }
}

using Re0Agent.Core.Entities;

namespace Re0Agent.Core.Services.Agent;

public sealed class KeplerOutputValidator
{
    public string ValidateOrFallback(string? output, IReadOnlyList<TimelineEvent> facts)
    {
        if (!string.IsNullOrWhiteSpace(output) && !ContainsRoleTakeover(output)) return output.Trim();
        var scene = facts.LastOrDefault()?.SceneId ?? "当前场景";
        var eventTypes = string.Join("、", facts.Select(item => item.EventType).Distinct(StringComparer.Ordinal));
        return $"镜头停留在{scene}。已确定的事件类型为：{eventTypes}。开普勒没有替任何角色增加行动或台词。";
    }

    private static bool ContainsRoleTakeover(string text) =>
        text.Contains("【角色", StringComparison.Ordinal)
        || text.Contains("代替", StringComparison.Ordinal)
        || text.Contains("CharacterAction:", StringComparison.OrdinalIgnoreCase)
        || text.Contains("<character>", StringComparison.OrdinalIgnoreCase);
}

using Re0Agent.Core.Services.Settings;

namespace Re0Agent.Core.Services.Agent;

/// <summary>
/// 基于世界书条目的别名字典，把同一角色的不同称呼归一到规范名。
/// 字典来源：每个 characters:* 条目的规范名（Comment 主体）+ 其 Keys 别名数组。
/// 例：罗兹瓦尔 / 梅扎斯 / 小丑 / 边境伯 -> 罗兹瓦尔·L·梅瑟斯
///     贝蒂 / 贝阿特丽丝 / 贝翠丝 -> 碧翠丝
/// </summary>
public sealed class CharacterNameResolver(IRagService ragService)
{
    /// <summary>常见敬称后缀，匹配前剥离，避免"罗兹瓦尔大人"被当成新角色。</summary>
    private static readonly string[] Honorifics =
        ["大人", "殿下", "阁下", "陛下", "先生", "小姐", "夫人", "桑", "酱", "君", "样", "卿"];

    public sealed record AliasGroup(string Canonical, IReadOnlyList<string> Aliases);

    public async Task<IReadOnlyList<AliasGroup>> LoadGroupsAsync(CancellationToken cancellationToken = default)
    {
        var entries = await ragService.ListAllEntriesAsync(cancellationToken);
        var groups = new List<AliasGroup>();

        foreach (var entry in entries)
        {
            var key = WorldBookCategory.GetKey(entry);
            if (!key.StartsWith("characters:", StringComparison.Ordinal)) continue;

            var canonical = key["characters:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(canonical)) continue;

            var aliases = new List<string> { canonical };
            aliases.AddRange(entry.Keys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim()));

            groups.Add(new AliasGroup(canonical, aliases.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        }

        return groups;
    }

    /// <summary>把任意称呼解析为规范名；字典未收录时返回 null。</summary>
    public static string? ResolveCanonical(IReadOnlyList<AliasGroup> groups, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var raw = name.Trim();
        var stripped = StripHonorific(raw);

        // 1) 精确匹配别名（含规范名），先原名后去敬称。
        foreach (var candidate in new[] { raw, stripped })
        {
            foreach (var group in groups)
            {
                if (group.Aliases.Any(a => string.Equals(a, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    return group.Canonical;
                }
            }
        }

        // 2) 兜底：别名作为子串出现在称呼里（别名长度≥2，避免单字误判）。
        foreach (var group in groups)
        {
            if (group.Aliases.Any(a => a.Length >= 2 && raw.Contains(a, StringComparison.OrdinalIgnoreCase)))
            {
                return group.Canonical;
            }
        }

        return null;
    }

    /// <summary>判断两个称呼是否指向同一角色。</summary>
    public static bool IsSameCharacter(IReadOnlyList<AliasGroup> groups, string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) return true;

        var ca = ResolveCanonical(groups, a);
        var cb = ResolveCanonical(groups, b);
        return ca is not null && cb is not null
            && string.Equals(ca, cb, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripHonorific(string name)
    {
        var n = name.Trim();
        foreach (var h in Honorifics)
        {
            if (n.Length > h.Length && n.EndsWith(h, StringComparison.Ordinal))
            {
                return n[..^h.Length];
            }
        }
        return n;
    }
}

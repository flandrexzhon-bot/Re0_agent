using Re0Agent.Core.Models;
using Re0Agent.Core.Services.Settings;

namespace Re0Agent.Core.Services.Agent;

/// <summary>
/// 预制角色 ID 字典。把角色的稳定身份 ID（写在世界书条目的 <see cref="WorldBookEntry.CharId"/>）
/// 与其规范名、别名互相映射。ID 是角色的权威身份，取代脆弱的「别名/外号」模糊匹配。
/// 位号（出场顺序）与此 ID 无关。
/// 数据源与世界书同源（<see cref="BlackTeaWorldBook.Entries"/>），无需额外文件。
/// </summary>
public sealed class CharacterIdRegistry
{
    private readonly Dictionary<string, int> _nameToId;
    private readonly Dictionary<int, string> _idToCanonical;

    public CharacterIdRegistry()
        : this(BlackTeaWorldBook.Entries)
    {
    }

    public CharacterIdRegistry(IEnumerable<WorldBookEntry> entries)
    {
        _nameToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _idToCanonical = new Dictionary<int, string>();

        foreach (var entry in entries)
        {
            if (entry.CharId <= 0)
            {
                continue;
            }

            var key = WorldBookCategory.GetKey(entry);
            if (!key.StartsWith("characters:", StringComparison.Ordinal))
            {
                continue;
            }

            var canonical = key["characters:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(canonical))
            {
                continue;
            }

            _idToCanonical[entry.CharId] = canonical;

            foreach (var alias in new[] { canonical }.Concat(entry.Keys))
            {
                var trimmed = alias?.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    _nameToId[trimmed] = entry.CharId;
                }
            }
        }
    }

    /// <summary>把任意称呼解析为稳定 ID；未收录返回 null。先精确，再子串兜底。</summary>
    public int? ResolveId(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var raw = name.Trim();
        if (_nameToId.TryGetValue(raw, out var id))
        {
            return id;
        }

        foreach (var (alias, aliasId) in _nameToId)
        {
            if (alias.Length >= 2 && raw.Contains(alias, StringComparison.OrdinalIgnoreCase))
            {
                return aliasId;
            }
        }

        return null;
    }

    /// <summary>把稳定 ID 解析为规范名；未收录返回 null。</summary>
    public string? ResolveName(int charId)
    {
        return _idToCanonical.TryGetValue(charId, out var name) ? name : null;
    }

    /// <summary>
    /// 为当前出场角色生成「ID｜名字」对照表文本，注入 GM 提示词。
    /// GM 叙事仍用名字，裁决/接力时用 ID 指代攻防双方。
    /// </summary>
    public string BuildRosterTable(IEnumerable<CharacterAgentProfile> profiles)
    {
        var rows = new List<string>();
        var seen = new HashSet<int>();

        foreach (var profile in profiles)
        {
            var id = ResolveId(profile.CharacterName);
            if (id is null || !seen.Add(id.Value))
            {
                continue;
            }

            var canonical = ResolveName(id.Value) ?? profile.CharacterName;
            rows.Add($"#{id.Value} = {canonical}");
        }

        return rows.Count == 0
            ? "（本场无可识别的预制角色）"
            : string.Join('\n', rows);
    }
}

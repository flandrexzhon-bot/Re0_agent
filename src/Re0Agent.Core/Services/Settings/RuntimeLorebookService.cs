using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;

namespace Re0Agent.Core.Services.Settings;

public sealed class RuntimeLorebookService(Re0AgentDbContext dbContext)
{
    public async Task<string> ActivateAsync(string query, int budgetCharacters = 12000, CancellationToken cancellationToken = default)
    {
        var sources = await dbContext.LorebookSources.AsNoTracking().OrderBy(item => item.SourceId).ToListAsync(cancellationToken);
        var active = new List<(int Order, string Position, string Content)>();
        var chapter = await dbContext.GlobalStates.AsNoTracking().Select(item => (int?)item.CurrentChapter)
            .FirstOrDefaultAsync(cancellationToken) ?? 1;
        var conditionalEntries = await dbContext.LorebookConditionEntries.AsNoTracking().OrderBy(item => item.SourceOrder).ToListAsync(cancellationToken);
        foreach (var entry in conditionalEntries.Where(item => MatchesFactPredicate(item.FactPredicate, chapter)))
            active.Add((entry.SourceOrder, "before", entry.Content));
        var activationText = query;
        for (var pass = 0; pass < 4; pass++)
        {
            var added = 0;
            foreach (var source in sources)
            {
                using var document = JsonDocument.Parse(source.Entries);
                var entries = document.RootElement.ValueKind == JsonValueKind.Object
                    ? document.RootElement.EnumerateObject().Select(item => item.Value)
                    : document.RootElement.EnumerateArray();
                var index = 0;
                foreach (var entry in entries)
                {
                    if (!IsActive(entry, activationText, source.SourceKey, index++)) continue;
                    var text = ReadString(entry, "content");
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    active.Add((ReadInt(entry, "insertion_order", index), ReadString(entry, "position") ?? "before", text));
                    activationText += "\n" + text;
                    added++;
                }
            }
            if (added == 0) break;
        }
        var ordered = active.OrderBy(item => item.Order).ToList();
        var before = ordered.Where(item => item.Position.Contains("before", StringComparison.OrdinalIgnoreCase));
        var after = ordered.Where(item => item.Position.Contains("after", StringComparison.OrdinalIgnoreCase));
        var middle = ordered.Except(before).Except(after);
        return string.Join("\n\n", before.Concat(middle).Concat(after).Select(item => item.Content))
            .Take(budgetCharacters).Aggregate(new System.Text.StringBuilder(), (builder, character) => builder.Append(character)).ToString();
    }

    private static bool MatchesFactPredicate(string predicate, int chapter)
    {
        var match = Regex.Match(predicate, @"legacy\.chapter\s*(?<op><=|>=|==|<|>)\s*(?<value>\d+)", RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups["value"].Value, out var expected)) return false;
        return match.Groups["op"].Value switch
        {
            "<" => chapter < expected, ">" => chapter > expected, "<=" => chapter <= expected,
            ">=" => chapter >= expected, "==" => chapter == expected, _ => false
        };
    }

    private static bool IsActive(JsonElement entry, string query, string sourceKey, int index)
    {
        if (entry.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False) return false;
        if (entry.TryGetProperty("constant", out var constant) && constant.ValueKind == JsonValueKind.True) return true;
        var keys = ReadStrings(entry, "keys");
        var secondary = ReadStrings(entry, "secondary_keys");
        var regex = entry.TryGetProperty("use_regex", out var useRegex) && useRegex.ValueKind == JsonValueKind.True;
        var primary = keys.Count == 0 || keys.Any(key => regex ? Regex.IsMatch(query, key, RegexOptions.IgnoreCase) : query.Contains(key, StringComparison.OrdinalIgnoreCase));
        if (!primary) return false;
        var selective = entry.TryGetProperty("selective", out var selectiveValue) && selectiveValue.ValueKind == JsonValueKind.True;
        if (selective && secondary.Count > 0 && !secondary.Any(key => query.Contains(key, StringComparison.OrdinalIgnoreCase))) return false;
        if (entry.TryGetProperty("probability", out var probability) && probability.TryGetInt32(out var chance) && chance < 100)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey + ":" + index + ":" + query));
            var hash = BitConverter.ToUInt32(bytes, 0) % 100;
            if (hash >= chance) return false;
        }
        return true;
    }

    private static List<string> ReadStrings(JsonElement entry, string name) => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList() : [];
    private static string? ReadString(JsonElement entry, string name) => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int ReadInt(JsonElement entry, string name, int fallback) => entry.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : fallback;
}

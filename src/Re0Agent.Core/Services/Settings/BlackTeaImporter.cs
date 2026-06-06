using System.Text.Json;

namespace Re0Agent.Core.Services.Settings;

public sealed class BlackTeaImporter : IBlackTeaImporter
{
    public async Task<IReadOnlyList<WorldBookEntry>> ImportAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(filePath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!TryReadEntries(document.RootElement, out var entriesElement))
        {
            return [];
        }

        var entries = new List<WorldBookEntry>();
        foreach (var entryElement in entriesElement.EnumerateArray())
        {
            entries.Add(new WorldBookEntry
            {
                Id = ReadInt(entryElement, "id"),
                Keys = ReadStringArray(entryElement, "keys"),
                SecondaryKeys = ReadStringArray(entryElement, "secondary_keys"),
                Comment = ReadString(entryElement, "comment"),
                Content = ReadString(entryElement, "content"),
                Constant = ReadBool(entryElement, "constant"),
                Selective = ReadBool(entryElement, "selective"),
                InsertionOrder = ReadInt(entryElement, "insertion_order"),
                Enabled = ReadBool(entryElement, "enabled", defaultValue: true),
                Position = ReadNullableString(entryElement, "position"),
                UseRegex = ReadBool(entryElement, "use_regex")
            });
        }

        return entries;
    }

    private static bool TryReadEntries(JsonElement root, out JsonElement entriesElement)
    {
        if (root.TryGetProperty("originalData", out var originalData)
            && originalData.TryGetProperty("entries", out entriesElement)
            && entriesElement.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (root.TryGetProperty("entries", out entriesElement)
            && entriesElement.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        entriesElement = default;
        return false;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var arrayElement)
            || arrayElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return arrayElement
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return ReadNullableString(element, propertyName) ?? string.Empty;
    }

    private static string? ReadNullableString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result)
            ? result
            : 0;
    }

    private static bool ReadBool(JsonElement element, string propertyName, bool defaultValue = false)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return defaultValue;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue
        };
    }
}

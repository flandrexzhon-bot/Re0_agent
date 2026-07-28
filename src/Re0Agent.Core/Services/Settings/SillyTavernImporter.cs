using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;

namespace Re0Agent.Core.Services.Settings;

public sealed record ImportedCard(
    int SourceId,
    string SourceKey,
    string Name,
    string? FirstMessage,
    IReadOnlyList<string> AlternateGreetings,
    string? CharacterBook);

public sealed class SillyTavernImporter(Re0AgentDbContext dbContext)
{
    public async Task<ImportedCard> ImportCharacterCardCharxAsync(
        string sourceKey,
        byte[] charx,
        CancellationToken cancellationToken = default)
    {
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(charx), System.IO.Compression.ZipArchiveMode.Read);
        var jsonEntry = archive.Entries.FirstOrDefault(item => item.FullName.EndsWith("card.json", StringComparison.OrdinalIgnoreCase))
            ?? archive.Entries.FirstOrDefault(item => item.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
        if (jsonEntry is null) throw new InvalidDataException("CHARX 中没有角色卡 JSON 元数据。");
        using var reader = new StreamReader(jsonEntry.Open(), Encoding.UTF8);
        var json = await reader.ReadToEndAsync(cancellationToken);
        return await ImportCharacterCardJsonAsync(sourceKey, json, cancellationToken);
    }

    public async Task<ImportedCard> ImportCharacterCardPngAsync(
        string sourceKey,
        byte[] png,
        CancellationToken cancellationToken = default)
    {
        var encoded = ReadCharaText(png);
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        return await ImportCharacterCardJsonAsync(sourceKey, json, cancellationToken);
    }

    public async Task<ImportedCard> ImportCharacterCardJsonAsync(
        string sourceKey,
        string json,
        CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var data = root.TryGetProperty("data", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object ? wrapped : root;
        var standard = new HashSet<string>(StringComparer.Ordinal)
        {
            "name", "description", "personality", "scenario", "first_mes", "alternate_greetings", "character_book", "creator", "character_version", "tags", "mes_example", "system_prompt", "post_history_instructions", "extensions"
        };
        var extensions = data.TryGetProperty("extensions", out var declaredExtensions)
            ? declaredExtensions.GetRawText()
            : JsonSerializer.Serialize(data.EnumerateObject().Where(item => !standard.Contains(item.Name)).ToDictionary(item => item.Name, item => item.Value));
        var alternateGreetings = data.TryGetProperty("alternate_greetings", out var greetings) && greetings.ValueKind == JsonValueKind.Array
            ? greetings.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList()
            : [];
        var card = new CharacterCardSource
        {
            SourceKey = sourceKey,
            Format = root.TryGetProperty("data", out _) ? "sillytavern-v2-json" : "sillytavern-v1-json",
            Name = ReadString(data, "name") ?? sourceKey,
            Description = ReadString(data, "description"),
            Personality = ReadString(data, "personality"),
            Scenario = ReadString(data, "scenario"),
            SystemPrompt = ReadString(data, "system_prompt"),
            PostHistoryInstructions = ReadString(data, "post_history_instructions"),
            ExampleDialogues = ReadString(data, "mes_example"),
            Creator = ReadString(data, "creator"),
            CharacterVersion = ReadString(data, "character_version"),
            Tags = data.TryGetProperty("tags", out var tags) ? tags.GetRawText() : null,
            FirstMessage = ReadString(data, "first_mes"),
            AlternateGreetings = JsonSerializer.Serialize(alternateGreetings),
            CharacterBook = data.TryGetProperty("character_book", out var book) ? book.GetRawText() : null,
            Extensions = extensions,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O")
        };
        var existing = await dbContext.CharacterCardSources.SingleOrDefaultAsync(item => item.SourceKey == sourceKey, cancellationToken);
        if (existing is not null) dbContext.CharacterCardSources.Remove(existing);
        dbContext.CharacterCardSources.Add(card);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new ImportedCard(card.SourceId, card.SourceKey, card.Name, card.FirstMessage, alternateGreetings, card.CharacterBook);
    }

    public async Task<int> ImportLorebookJsonAsync(
        string sourceKey,
        string json,
        CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var entries = root.TryGetProperty("entries", out var directEntries) ? directEntries : root;
        var known = new HashSet<string>(StringComparer.Ordinal) { "entries", "name", "description", "extensions" };
        var extensions = JsonSerializer.Serialize(root.EnumerateObject().Where(item => !known.Contains(item.Name)).ToDictionary(item => item.Name, item => item.Value));
        var source = new LorebookSource
        {
            SourceKey = sourceKey,
            Format = "sillytavern-lorebook-json",
            Entries = entries.GetRawText(),
            Extensions = root.TryGetProperty("extensions", out var declared) ? declared.GetRawText() : extensions,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O")
        };
        var existing = await dbContext.LorebookSources.SingleOrDefaultAsync(item => item.SourceKey == sourceKey, cancellationToken);
        if (existing is not null) dbContext.LorebookSources.Remove(existing);
        dbContext.LorebookSources.Add(source);
        await dbContext.SaveChangesAsync(cancellationToken);
        return source.SourceId;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string ReadCharaText(byte[] png)
    {
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length < signature.Length || !png.AsSpan(0, signature.Length).SequenceEqual(signature))
            throw new InvalidDataException("不是有效的 PNG 角色卡文件。");
        var offset = signature.Length;
        while (offset + 12 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            if (length < 0 || offset + 12 + length > png.Length) break;
            var data = png.AsSpan(offset + 8, length);
            if (type == "tEXt")
            {
                var separator = data.IndexOf((byte)0);
                if (separator >= 0 && Encoding.ASCII.GetString(data[..separator]) == "chara")
                    return Encoding.ASCII.GetString(data[(separator + 1)..]);
            }
            else if (type == "iTXt")
            {
                var separator = data.IndexOf((byte)0);
                if (separator >= 0 && Encoding.UTF8.GetString(data[..separator]) == "chara")
                {
                    var body = data[(separator + 1)..];
                    var first = body.IndexOf((byte)0);
                    if (first >= 0) body = body[(first + 1)..];
                    var second = body.IndexOf((byte)0);
                    if (second >= 0) body = body[(second + 1)..];
                    var third = body.IndexOf((byte)0);
                    if (third >= 0) body = body[(third + 1)..];
                    return Encoding.UTF8.GetString(body);
                }
            }
            offset += 12 + length;
        }
        throw new InvalidDataException("PNG 中没有找到 chara 角色卡元数据。");
    }
}

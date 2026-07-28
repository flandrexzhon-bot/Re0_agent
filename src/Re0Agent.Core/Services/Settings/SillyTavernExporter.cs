using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;

namespace Re0Agent.Core.Services.Settings;

public sealed class SillyTavernExporter(Re0AgentDbContext dbContext)
{
    public async Task<string> ExportCharacterCardJsonAsync(string sourceKey, CancellationToken cancellationToken = default)
    {
        var card = await dbContext.CharacterCardSources.SingleAsync(item => item.SourceKey == sourceKey, cancellationToken);
        var data = new JsonObject
        {
            ["name"] = card.Name,
            ["description"] = card.Description,
            ["personality"] = card.Personality,
            ["scenario"] = card.Scenario,
            ["system_prompt"] = card.SystemPrompt,
            ["post_history_instructions"] = card.PostHistoryInstructions,
            ["mes_example"] = card.ExampleDialogues,
            ["creator"] = card.Creator,
            ["character_version"] = card.CharacterVersion,
            ["tags"] = string.IsNullOrWhiteSpace(card.Tags) ? null : JsonNode.Parse(card.Tags),
            ["first_mes"] = card.FirstMessage,
            ["alternate_greetings"] = JsonNode.Parse(card.AlternateGreetings),
            ["extensions"] = JsonNode.Parse(card.Extensions) ?? new JsonObject()
        };
        if (!string.IsNullOrWhiteSpace(card.CharacterBook)) data["character_book"] = JsonNode.Parse(card.CharacterBook);
        var root = new JsonObject { ["spec"] = "chara_card_v2", ["spec_version"] = "2.0", ["data"] = data };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<string> ExportLorebookJsonAsync(string sourceKey, CancellationToken cancellationToken = default)
    {
        var source = await dbContext.LorebookSources.SingleAsync(item => item.SourceKey == sourceKey, cancellationToken);
        var root = new JsonObject
        {
            ["name"] = source.SourceKey,
            ["entries"] = JsonNode.Parse(source.Entries),
            ["extensions"] = JsonNode.Parse(source.Extensions) ?? new JsonObject()
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<byte[]> ExportCharacterCardPngAsync(string sourceKey, CancellationToken cancellationToken = default)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(await ExportCharacterCardJsonAsync(sourceKey, cancellationToken)));
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        WriteChunk(output, "IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0]);
        WriteChunk(output, "tEXt", Encoding.ASCII.GetBytes("chara\0" + encoded));
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
                zlib.Write([0, 0, 0, 0, 0]);
            WriteChunk(output, "IDAT", compressed.ToArray());
        }
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = Crc32(typeBytes, data);
        Span<byte> checksum = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        output.Write(checksum);
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xffffffff;
        foreach (var value in type.Concat(data))
        {
            crc ^= value;
            for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ (0xedb88320u & unchecked((uint)-(int)(crc & 1)));
        }
        return ~crc;
    }
}

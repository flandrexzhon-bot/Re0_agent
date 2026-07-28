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
        if (archive.Entries.Any(item => item.FullName.Contains("..", StringComparison.Ordinal)
            || Path.IsPathRooted(item.FullName)))
            throw new InvalidDataException("CHARX 包含不安全的档案路径。");
        var cardEntries = archive.Entries
            .Where(item => item.FullName.Equals("card.json", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (cardEntries.Count != 1) throw new InvalidDataException("CHARX 必须且只能包含一个根目录 card.json。");
        var jsonEntry = cardEntries[0];
        if (jsonEntry.Length is <= 0 or > 10 * 1024 * 1024) throw new InvalidDataException("CHARX 的 card.json 大小无效。");
        using var reader = new StreamReader(jsonEntry.Open(), Encoding.UTF8);
        var json = await reader.ReadToEndAsync(cancellationToken);
        ValidateMinimumCard(json, requireFirstMessage: true);
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
        ValidateMinimumCard(json, requireFirstMessage: false);
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
        await EnsureImportedCharacterAsync(card, cancellationToken);
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

    private async Task EnsureImportedCharacterAsync(CharacterCardSource card, CancellationToken cancellationToken)
    {
        var npc = await dbContext.ImportantNpcs.SingleOrDefaultAsync(item => item.Name == card.Name, cancellationToken);
        if (npc is null)
        {
            var rowId = (await dbContext.ImportantNpcs.MaxAsync(item => (int?)item.RowId, cancellationToken) ?? 0) + 1;
            var charId = (await dbContext.ImportantNpcs.MaxAsync(item => (int?)item.CharId, cancellationToken) ?? 0) + 1;
            var scene = await dbContext.GlobalStates.Select(item => item.CurrentLocation).FirstOrDefaultAsync(cancellationToken) ?? "未指定";
            npc = new ImportantNpc
            {
                RowId = rowId,
                CharId = charId,
                Name = card.Name,
                Gender = "未知",
                Age = 0,
                BriefIntro = Limit(card.Description ?? card.Personality ?? "导入角色", 60),
                Appearance = Limit(card.Description ?? "外观未提供", 60),
                IdentityText = Limit(card.Personality ?? "导入角色", 40),
                BaseAttributes = "未设定",
                SpecialAttributes = null,
                LocationName = scene,
                RelationsText = null,
                InteractionOptions = "交谈",
                PastExperience = card.Scenario ?? "暂无已提交经历。"
            };
            dbContext.ImportantNpcs.Add(npc);
        }

        var characterId = $"npc:{npc.RowId}";
        var agency = await dbContext.CharacterAgencyStates.SingleOrDefaultAsync(item => item.CharacterId == characterId, cancellationToken);
        if (agency is null)
        {
            dbContext.CharacterAgencyStates.Add(new CharacterAgencyState
            {
                CharacterId = characterId,
                SceneId = npc.LocationName,
                CurrentGoal = card.Scenario ?? card.Personality ?? "依据自身目标生活。",
                NextActionWorldTime = DateTimeOffset.UtcNow.ToString("O"),
                Fidelity = "foreground",
                Status = "Active"
            });
        }

        if (!await dbContext.AgentConfig.AnyAsync(item => item.AgentName == card.Name, cancellationToken))
        {
            var template = await dbContext.AgentConfig.AsNoTracking()
                .Where(item => item.AgentType == "Character")
                .OrderBy(item => item.ConfigId).FirstOrDefaultAsync(cancellationToken);
            dbContext.AgentConfig.Add(new AgentConfig
            {
                ConfigId = (await dbContext.AgentConfig.MaxAsync(item => (int?)item.ConfigId, cancellationToken) ?? 0) + 1,
                AgentType = "Character",
                AgentName = card.Name,
                ApiEndpoint = template?.ApiEndpoint ?? "",
                ApiKey = template?.ApiKey ?? "",
                ModelName = template?.ModelName ?? "",
                Temperature = template?.Temperature ?? .7,
                MaxTokens = template?.MaxTokens ?? 4096,
                MaxInputTokens = template?.MaxInputTokens ?? 4096,
                SystemPrompt = BuildCharacterPrompt(card),
                Enabled = template is null ? 0 : template.Enabled,
                EnableThinking = template?.EnableThinking ?? false,
                ReasoningEffort = template?.ReasoningEffort ?? "medium",
                AutoRetry = template?.AutoRetry ?? true
            });
        }
    }

    private static void ValidateMinimumCard(string json, bool requireFirstMessage)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var data = root.TryGetProperty("data", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object ? wrapped : root;
        if (string.IsNullOrWhiteSpace(ReadString(data, "name"))) throw new InvalidDataException("角色卡缺少 name。");
        if (requireFirstMessage && string.IsNullOrWhiteSpace(ReadString(data, "first_mes")))
            throw new InvalidDataException("CHARX 角色卡缺少 first_mes。");
    }

    private static string BuildCharacterPrompt(CharacterCardSource card) => string.Join('\n', new[]
    {
        $"你只扮演 {card.Name}。",
        card.Description,
        card.Personality,
        card.SystemPrompt,
        card.PostHistoryInstructions
    }.Where(item => !string.IsNullOrWhiteSpace(item)));

    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];

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

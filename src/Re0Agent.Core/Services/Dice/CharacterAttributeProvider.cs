using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;
using Re0Agent.Core.Entities;

namespace Re0Agent.Core.Services.Dice;

public sealed partial class CharacterAttributeProvider(Re0AgentDbContext dbContext)
{
    /// <summary>按名字（或 &lt;user&gt;/主角 等指代）查找属性；返回值带上角色稳定 ID 与护甲。</summary>
    public async Task<CharacterAttribute?> FindAttributeAsync(
        string? characterName,
        string? attributeName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(characterName) || string.IsNullOrWhiteSpace(attributeName))
        {
            return null;
        }

        var protagonist = await dbContext.ProtagonistInfo.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var resolvedName = ResolveCharacterName(characterName, protagonist?.Name);

        if (protagonist is not null && string.Equals(resolvedName, protagonist.Name, StringComparison.Ordinal))
        {
            return BuildProtagonistAttribute(protagonist, attributeName);
        }

        var npc = await dbContext.ImportantNpcs
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Name == resolvedName, cancellationToken);

        return npc is null ? null : BuildNpcAttribute(npc, attributeName);
    }

    /// <summary>按稳定 ID 查找属性（战斗/接力的权威寻址方式）。</summary>
    public async Task<CharacterAttribute?> FindAttributeByIdAsync(
        int charId,
        string? attributeName,
        CancellationToken cancellationToken = default)
    {
        if (charId <= 0 || string.IsNullOrWhiteSpace(attributeName))
        {
            return null;
        }

        var protagonist = await dbContext.ProtagonistInfo.AsNoTracking()
            .FirstOrDefaultAsync(p => p.CharId == charId, cancellationToken);
        if (protagonist is not null)
        {
            return BuildProtagonistAttribute(protagonist, attributeName);
        }

        var npc = await dbContext.ImportantNpcs.AsNoTracking()
            .FirstOrDefaultAsync(n => n.CharId == charId, cancellationToken);
        return npc is null ? null : BuildNpcAttribute(npc, attributeName);
    }

    private static CharacterAttribute? BuildProtagonistAttribute(ProtagonistInfo protagonist, string attributeName)
    {
        var value = FindAttributeValue(attributeName, protagonist.BaseAttributes, protagonist.SpecialAttributes);
        return value is null
            ? null
            : new CharacterAttribute
            {
                CharacterName = protagonist.Name,
                AttributeName = attributeName,
                Value = value.Value,
                IsPlayerControlled = true,
                CharId = protagonist.CharId,
                Armor = protagonist.Armor
            };
    }

    private static CharacterAttribute? BuildNpcAttribute(ImportantNpc npc, string attributeName)
    {
        var value = FindAttributeValue(attributeName, npc.BaseAttributes, npc.SpecialAttributes);
        return value is null
            ? null
            : new CharacterAttribute
            {
                CharacterName = npc.Name,
                AttributeName = attributeName,
                Value = value.Value,
                IsPlayerControlled = false,
                CharId = npc.CharId,
                Armor = npc.Armor
            };
    }

    private static string ResolveCharacterName(string characterName, string? protagonistName)
    {
        if ((characterName is "<user>" or "user" or "主角") && !string.IsNullOrWhiteSpace(protagonistName))
        {
            return protagonistName;
        }

        return characterName;
    }

    private static int? FindAttributeValue(
        string attributeName,
        params string?[] attributeTexts)
    {
        foreach (var attributeText in attributeTexts.Where(text => !string.IsNullOrWhiteSpace(text)))
        {
            foreach (Match match in AttributeRegex().Matches(attributeText!))
            {
                var name = match.Groups["name"].Value.Trim();
                if (!string.Equals(name, attributeName, StringComparison.Ordinal))
                {
                    continue;
                }

                return int.Parse(match.Groups["value"].Value);
            }
        }

        return null;
    }

    [GeneratedRegex("(?<name>[^:：;；\\s]+)\\s*[:：]\\s*(?<value>\\d+)", RegexOptions.Compiled)]
    private static partial Regex AttributeRegex();
}

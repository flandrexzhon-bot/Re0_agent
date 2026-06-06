using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Re0Agent.Core.Database;

namespace Re0Agent.Core.Services.Dice;

public sealed partial class CharacterAttributeProvider(Re0AgentDbContext dbContext)
{
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
            var value = FindAttributeValue(
                attributeName,
                protagonist.BaseAttributes,
                protagonist.SpecialAttributes);

            return value is null
                ? null
                : new CharacterAttribute
                {
                    CharacterName = protagonist.Name,
                    AttributeName = attributeName,
                    Value = value.Value,
                    IsPlayerControlled = true
                };
        }

        var npc = await dbContext.ImportantNpcs
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Name == resolvedName, cancellationToken);

        if (npc is null)
        {
            return null;
        }

        var npcValue = FindAttributeValue(attributeName, npc.BaseAttributes, npc.SpecialAttributes);
        return npcValue is null
            ? null
            : new CharacterAttribute
            {
                CharacterName = npc.Name,
                AttributeName = attributeName,
                Value = npcValue.Value,
                IsPlayerControlled = false
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

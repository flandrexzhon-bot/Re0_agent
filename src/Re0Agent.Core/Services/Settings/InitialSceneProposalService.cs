using System.Text.RegularExpressions;
using Re0Agent.Core.Entities;
using Re0Agent.Core.Models;

namespace Re0Agent.Core.Services.Settings;

public sealed partial class InitialSceneProposalService
{
    public InitialSceneProposal Build(CharacterCardSource card, string protagonistName)
    {
        var sourceText = string.Join('\n', new[] { card.Scenario, card.Description, card.FirstMessage }.Where(text => !string.IsNullOrWhiteSpace(text)));
        var scene = SceneRegex().Match(sourceText).Groups["scene"].Value.Trim();
        var names = new List<string> { card.Name };
        if (!string.Equals(card.Name, protagonistName, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(protagonistName))
            names.Add(protagonistName);
        return new InitialSceneProposal(
            card.SourceKey,
            string.IsNullOrWhiteSpace(scene) ? "unspecified_locale" : scene,
            "world_epoch_start",
            names,
            [],
            []);
    }

    [GeneratedRegex("(?:地点|场景|场所|位于)[:：\\s]*(?<scene>[^，。；;\\r\\n]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SceneRegex();
}

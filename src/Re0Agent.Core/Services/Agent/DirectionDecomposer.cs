using System.Text.RegularExpressions;
using Re0Agent.Core.Models;

namespace Re0Agent.Core.Services.Agent;

public sealed partial class DirectionDecomposer
{
    public PlayerDirection Decompose(string directionId, string rawText)
    {
        var text = rawText.Trim();
        if (text.Length == 0)
        {
            return new(directionId, rawText, "none", [], null, [], "Blocked", "导演指令为空。");
        }

        var scene = SceneRegex().Match(text);
        var actors = ActorRegex().Match(text).Groups["actors"].Value
            .Split(['、', ',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var preconditions = new List<string>();
        if (scene.Success) preconditions.Add($"scene_exists:{scene.Groups["scene"].Value.Trim()}");
        preconditions.AddRange(actors.Select(actor => $"actor_exists:{actor}"));
        return new(
            directionId,
            rawText,
            text,
            actors,
            scene.Success ? scene.Groups["scene"].Value.Trim() : null,
            preconditions,
            "Realizing",
            null);
    }

    [GeneratedRegex("(?:前往|转到|切到|场景)[:：\\s]*(?<scene>[^，。；;]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SceneRegex();

    [GeneratedRegex("(?:让|要求|指示)(?<actors>[^，。；;]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ActorRegex();
}

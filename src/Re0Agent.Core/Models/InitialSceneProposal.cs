namespace Re0Agent.Core.Models;

public sealed record InitialSceneProposal(
    string SourceKey,
    string SceneId,
    string LogicalWorldTime,
    IReadOnlyList<string> PresentCharacterNames,
    IReadOnlyList<string> DeclaredRelationships,
    IReadOnlyList<string> Conflicts);

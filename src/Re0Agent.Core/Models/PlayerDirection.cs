namespace Re0Agent.Core.Models;

public sealed record PlayerDirection(
    string DirectionId,
    string RawText,
    string Intent,
    IReadOnlyList<string> RequestedActors,
    string? RequestedScene,
    IReadOnlyList<string> Preconditions,
    string Status,
    string? BlockReason);

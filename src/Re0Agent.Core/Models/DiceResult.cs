namespace Re0Agent.Core.Models;

public sealed class DiceResult
{
    public required string Command { get; init; }
    public string? RollerName { get; init; }
    public string? AttributeName { get; init; }
    public int? Roll { get; init; }
    public int? Target { get; init; }
    public required string Outcome { get; init; }
    public string? Detail { get; init; }
    public bool? IsSuccess { get; init; }
    public string? SuccessLevel { get; init; }
    public string? RequiredLevel { get; init; }
    public IReadOnlyList<int> Rolls { get; init; } = [];
    public int? TargetAfterModifiers { get; init; }
    public string? Error { get; init; }
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
}

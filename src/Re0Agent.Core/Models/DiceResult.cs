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
}

using System.Text.Json;
using Re0Agent.Core.Entities;

namespace Re0Agent.Core.Services.Agent;

public sealed record PacingFeatures(
    double Tension,
    double InformationDensity,
    double ConsequenceDensity,
    int CameraAge,
    int SpeakerRun,
    double PlayerLoad);

public sealed class PacingFeatureExtractor
{
    public PacingFeatures Extract(IReadOnlyList<TimelineEvent> events)
    {
        var window = events.TakeLast(12).ToList();
        if (window.Count == 0) return new(0, 0, 0, 0, 0, 0);
        var tension = window.Select(ReadIntensity).Aggregate(0d, (current, value) => current * .72 + value * .28);
        var information = Math.Clamp(window.Average(item => Math.Min(item.Content.Length, 1200)) / 1200d, 0, 1);
        var consequence = Math.Clamp(window.Count(item => !string.IsNullOrWhiteSpace(item.StateChangeSet)) / (double)window.Count, 0, 1);
        var actor = window.Last().ActorId;
        var speakerRun = actor is null ? 0 : window.AsEnumerable().Reverse().TakeWhile(item => item.ActorId == actor).Count();
        var scene = window.Last().SceneId;
        var cameraAge = window.AsEnumerable().Reverse().TakeWhile(item => item.SceneId == scene).Count();
        var playerLoad = Math.Clamp(window.Count(item => item.EventType is "PlayerInput" or "PlayerDirection") / 4d, 0, 1);
        return new(tension, information, consequence, cameraAge, speakerRun, playerLoad);
    }

    private static double ReadIntensity(TimelineEvent item)
    {
        if (string.IsNullOrWhiteSpace(item.PacingMetadata)) return item.EventType is "RuleResolution" or "WorldRewindCommitted" ? .8 : .3;
        try
        {
            using var json = JsonDocument.Parse(item.PacingMetadata);
            return json.RootElement.TryGetProperty("intensity", out var value) && value.TryGetDouble(out var result) ? Math.Clamp(result, 0, 1) : .3;
        }
        catch (JsonException) { return .3; }
    }
}

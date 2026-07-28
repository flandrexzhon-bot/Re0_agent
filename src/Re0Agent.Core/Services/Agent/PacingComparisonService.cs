using Re0Agent.Core.Models;

namespace Re0Agent.Core.Services.Agent;

public enum PacingBaselineMode { UnpacedContinuous, DeterministicPacing, FullDirector }

public sealed record PacingBaselineResult(
    PacingBaselineMode Mode,
    int ScenarioCount,
    int BoundaryViolations,
    int MissingInterventions,
    IReadOnlyList<string> Failures)
{
    public bool Passed => BoundaryViolations == 0 && MissingInterventions == 0 && Failures.Count == 0;
}

public sealed record PacingComparisonReport(IReadOnlyList<PacingBaselineResult> Baselines);

public sealed class PacingComparisonService(PacingFeatureExtractor extractor)
{
    public PacingComparisonReport CompareFixedFixtures(IReadOnlyDictionary<string, BeatPlan> recordedDirectorPlans)
    {
        var unpaced = EvaluatePolicy(PacingBaselineMode.UnpacedContinuous, recordedDirectorPlans);
        var deterministic = EvaluatePolicy(PacingBaselineMode.DeterministicPacing, recordedDirectorPlans);
        var director = EvaluatePolicy(PacingBaselineMode.FullDirector, recordedDirectorPlans);
        return new([unpaced, deterministic, director]);
    }

    private PacingBaselineResult EvaluatePolicy(
        PacingBaselineMode mode,
        IReadOnlyDictionary<string, BeatPlan> directorPlans)
    {
        var boundaryViolations = 0;
        var missingInterventions = 0;
        var failures = new List<string>();
        foreach (var scenario in PacingBaselineScenarios.All)
        {
            var events = PacingBaselineScenarios.Materialize(scenario);
            var features = extractor.Extract(events);
            var state = BuildCalibrationState(features, scenario.Labels);
            var deterministic = PaceGovernor.DecideDeterministically(state);
            switch (mode)
            {
                case PacingBaselineMode.UnpacedContinuous:
                    if (scenario.Labels.Rushed || scenario.Labels.Dragging || features.SpeakerRun > 3) missingInterventions++;
                    break;
                case PacingBaselineMode.DeterministicPacing:
                    if (scenario.Labels.Rushed && !deterministic.RequiresBreathingBeat) missingInterventions++;
                    if (scenario.Labels.Dragging && !deterministic.RequiresProgressBeat && features.SpeakerRun <= 3) missingInterventions++;
                    if (deterministic.MaximumIntensity is < 0 or > 1) boundaryViolations++;
                    break;
                case PacingBaselineMode.FullDirector:
                    if (!directorPlans.TryGetValue(scenario.Name, out var plan))
                    {
                        failures.Add($"{scenario.Name}:missing_recorded_plan");
                        break;
                    }
                    var legalIds = events.Select(item => item.EventId).ToHashSet(StringComparer.Ordinal);
                    if (plan.CandidateEventIds.Count == 0 || plan.CandidateEventIds.Any(id => !legalIds.Contains(id))) boundaryViolations++;
                    if (plan.RequestedIntensity < 0 || plan.RequestedIntensity > deterministic.MaximumIntensity) boundaryViolations++;
                    if (scenario.Labels.Rushed && plan.RequestedIntensity > .5) missingInterventions++;
                    if (scenario.Labels.Dragging && plan.TimeAdvancePolicy == "hold") missingInterventions++;
                    break;
            }
        }
        return new PacingBaselineResult(mode, PacingBaselineScenarios.All.Count, boundaryViolations, missingInterventions, failures);
    }

    private static PacingState BuildCalibrationState(PacingFeatures features, PacingFixtureLabels labels) => new(
        features.Tension,
        features.Tension switch { < .3 => "Low", < .7 => "Medium", _ => "High" },
        features.InformationDensity,
        features.ConsequenceDensity,
        1,
        labels.Rushed ? 1.5 : 0,
        labels.Dragging && features.SpeakerRun <= 3 ? 1.5 : 0,
        features.CameraAge,
        features.SpeakerRun,
        features.PlayerLoad,
        PacePhase.Relax,
        3);
}

namespace Re0Agent.Core.Models;

public sealed record BeatPlan(
    string PlanId,
    string DramaticPurpose,
    IReadOnlyList<string> CandidateEventIds,
    IReadOnlyList<string> CandidateCharacterIds,
    string? ShotSuggestion,
    double RequestedIntensity,
    string TimeAdvancePolicy,
    IReadOnlyList<string> RevealBoundaries,
    string Rationale);

public enum PacePhase { Build, Sustain, Fade, Relax }

public sealed record PacingState(
    double Tension,
    string TensionBand,
    double InformationDensity,
    double ConsequenceDensity,
    int UnresolvedHooks,
    double BreathingDebt,
    double ProgressDebt,
    int CameraAge,
    int SpeakerRun,
    double PlayerLoad,
    PacePhase Phase,
    int PhaseAge);

public sealed record PaceDecision(double MaximumIntensity, bool RequiresBreathingBeat, bool RequiresProgressBeat, string Reason);

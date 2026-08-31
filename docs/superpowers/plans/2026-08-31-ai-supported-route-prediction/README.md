# AI-Supported Route-Time Prediction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a local, rider-specific learned effort adjustment that can improve Typical and Today route-time predictions while preserving the deterministic physics result as the permanent comparison and fallback.

**Architecture:** Weather-corrected chronological replays create one leak-free example per ride. Small application-owned additive regressors learn a bounded log-power multiplier; a validation-calibrated nearest-neighbour gate limits serving to supported routes, and the existing physics simulator reruns with the multiplier through `IPowerTargetPolicy`.

**Tech Stack:** .NET 10, C# 14, ASP.NET Core minimal APIs, EF Core 10/Npgsql/PostgreSQL, Blazor WebAssembly, xUnit, bUnit, application-owned numerical regression code, and the weather-aware services delivered by the prerequisite plan.

**Spec:** `docs/superpowers/specs/2026-08-31-ai-supported-route-time-prediction-design.md`

## Global Constraints

- Complete and merge every task in `docs/superpowers/plans/2026-08-31-weather-aware-training/` before starting Task 01.
- Read the complete accepted spec, this overview, and the current task file before editing.
- Do not execute on `main`. Use one feature branch/worktree. Task 01 uses `git push -u origin HEAD` when no upstream exists; later tasks use `git push`.
- Before every task, run `git status --short`; stop if it contains changes not produced by that task.
- Follow TDD exactly: add the named failing test, run it and observe the expected failure, implement the minimum complete behaviour, then rerun focused and regression tests.
- AI training and inference are local. Add no hosted model, LLM, Python service, network call, or opaque executable model blob.
- Weather-ready whole rides are the independent evidence unit. Never split samples or segments randomly across train/validation boundaries.
- Target-ride power, heart rate, cadence, completion time, and every later ride are forbidden input features for that target ride.
- Historical AI replay always uses the held-out ride's persisted weather. Ordinary new predictions remain calm/dry.
- The deterministic prediction is calculated first. Unsupported, unavailable, invalid, stale, or failed AI always returns that valid deterministic result.
- Typical is the default request mode. Today falls back in order: AI Today, AI Typical, deterministic.
- Do not alter explicit pacing-adjustment execution; it continues to use its existing captured baseline and policies.
- Algorithm constants are code-owned and recorded with the model version. Configuration may disable building/serving but may not change learned semantics.
- Do not hand-edit `Narrative.md`. The decision-bearing implementation PR requires `narrative-required` and the exact `## Narrative Context`, `## Narrative Decision`, and `## Narrative Consequences` headings before merge.
- Every task ends with fresh verification, one focused commit, a successful push, and empty `git status --short`. Never combine two task files in one commit.

## Post-Weather Interfaces Assumed

The weather plan is authoritative if implementation details differ. This series expects these completed interfaces:

```csharp
public sealed record WeatherActivityEvidence(
    Guid ActivityId,
    CleanedActivity Activity,
    WeatherTimeline Timeline,
    WeatherResolvedActivity Resolved);

public interface ITrainingActivityRepository
{
    Task<IReadOnlyList<TrainingActivityModelEvidence>> GetModelEvidenceAsync(
        CancellationToken cancellationToken);
}

public sealed class TimelineRouteEnvironment(WeatherTimeline timeline) : IRouteEnvironment
{
    public WeatherCondition Resolve(PredictionRouteSegment segment, DateTimeOffset at);
}

PredictionResult IRoutePredictor.Predict(
    PredictionRoute route,
    RiderProfile profile,
    RiderModel model,
    IPowerTargetPolicy? powerTargetPolicy = null,
    CancellationToken cancellationToken = default,
    PredictionEnvironment? environment = null);
```

`BuildModelJobHandler.AlgorithmVersion` is `weather-v1`, only weather-ready activities enter the model, and a successful build returns/persists an immutable `RiderModelSnapshot`.

## Stable AI Versions and Bounds

These values are fixed for the first implementation and must be persisted with artifacts:

```csharp
public static class AiAlgorithmVersions
{
    public const string FeatureSchema = "ai-features-v1";
    public const string Replay = "ai-replay-weather-v1";
    public const string Model = "ai-effort-v1";
}

public static class AiModelLimits
{
    public const int EvaluationRideCount = 30;
    public const int SeedRideCount = 15;
    public const int MinimumOuterFolds = 15;
    public const int MinimumInnerTrainingExamples = 8;
    public const int NeighbourCount = 5;
    public const int MinimumSupportCalibrationPoints = 5;
    public const double SolverMinimumMultiplier = 0.50;
    public const double SolverMaximumMultiplier = 1.50;
    public const double ServingMinimumMultiplier = 0.75;
    public const double ServingMaximumMultiplier = 1.25;
    public const double MinimumRelativeMedianImprovement = 0.10;
    public const double MinimumAbsoluteMedianImprovement = 0.01;
    public const double MaximumAbsoluteMedianBias = 0.03;
}
```

## Stable Cross-Task Types

Task 01 owns the domain enums and readiness records:

```csharp
public enum PredictionMode { Typical, Today }
public enum PredictionEffectiveMode { Deterministic, AiTypical, AiToday }
public enum AiReadinessState
{
    CollectingEvidence,
    ReadyToEvaluate,
    Evaluating,
    AiSupported,
    BaselineStillBest,
    Reevaluating
}
public enum AiPublicationState { Rejected, Published }
public enum TrainingHistoryConfirmationSource { GarminCheck, ManualConfirmation }

public sealed record AiReadinessContributor(int Current, int Target, double Points, double MaximumPoints);
public sealed record AiReadinessSnapshot(
    double Percentage,
    AiReadinessState State,
    AiReadinessContributor RideCount,
    AiReadinessContributor DurationVariety,
    AiReadinessContributor TerrainVariety,
    string? StrongestEvidenceCode,
    string? NextEvidenceCode,
    bool CanEvaluate);
```

Tasks 03-07 own fixed-order feature, model, and support records:

```csharp
public sealed record AiFeatureVector(string SchemaVersion, IReadOnlyList<double> Values);
public sealed record AiTrainingState(AiFeatureVector Features, bool HasFortyTwoDaysHistory);
public sealed record AiRegressorArtifact(
    string Kind,
    string FeatureSchemaVersion,
    double Intercept,
    IReadOnlyList<double> Medians,
    IReadOnlyList<double> Scales,
    IReadOnlyList<double> LinearCoefficients,
    IReadOnlyList<AiSmoothTerm> SmoothTerms);
public sealed record AiSmoothTerm(int FeatureIndex, IReadOnlyList<double> Knots, IReadOnlyList<double> Coefficients);

public sealed record AiValidationMetrics(
    int FoldCount,
    double MedianAbsolutePercentageError,
    double P90AbsolutePercentageError,
    double MedianSignedPercentageError);

public sealed record AiFeatureContribution(
    string Code,
    string Direction,
    double LogEffect);

public sealed record AiRouteSupportDecision(
    bool Supported,
    double? MatchPercentage,
    int NeighbourCount,
    string? ReasonCode,
    double? ComparableMedianApe,
    double? ComparableP90Ape);
```

Task 08 owns the immutable persisted aggregate consumed by Tasks 09-13:

```csharp
public sealed record RiderAiModelSnapshot(
    Guid Id,
    DateTimeOffset CreatedAt,
    AiPublicationState PublicationState,
    string AlgorithmVersion,
    string FeatureSchemaVersion,
    Guid TrainingRiderModelId,
    DateTimeOffset TrainingStartedAt,
    DateTimeOffset TrainingEndedAt,
    string CompatibleRiderModelAlgorithmVersion,
    RiderProfile ProfileSnapshot,
    AiReadinessSnapshot Readiness,
    AiRegressorArtifact? TypicalArtifact,
    AiRegressorArtifact? TodayArtifact,
    AiRouteSupportArtifact? RouteSupport,
    AiStateSupportRanges? TodayStateSupport,
    AiValidationMetrics? DeterministicMetrics,
    AiValidationMetrics? TypicalMetrics,
    AiValidationMetrics? TodayMetrics,
    double ObservedMinimumMultiplier,
    double ObservedMaximumMultiplier,
    string? RejectionReason);
```

If a compile-time need forces one of these names or signatures to change, update this README and every later unimplemented task file in the same commit. Do not allow neighbouring tasks to invent aliases.

## Target File Map

```text
src/RouteTimer.Domain/Ai/
  AiAlgorithmVersions.cs
  AiReadiness.cs
  AiModelArtifacts.cs
  AiValidation.cs
src/RouteTimer.Domain/Predictions/
  PredictionMode.cs
  PredictionAiMetadata.cs
src/RouteTimer.Services/Ai/
  Readiness/
  Features/
  Replay/
  Models/
  Support/
  Training/
  Prediction/
src/RouteTimer.Services/Persistence/
  IAiTrainingExampleRepository.cs
  IRiderAiModelRepository.cs
  ITrainingHistoryStateRepository.cs
src/RouteTimer.Persistence/Entities/
  AiTrainingExampleEntity.cs
  RiderAiModelEntity.cs
  TrainingHistoryStateEntity.cs
src/RouteTimer.Persistence/Repositories/
  AiTrainingExampleRepository.cs
  RiderAiModelRepository.cs
  TrainingHistoryStateRepository.cs
src/RouteTimer.Contracts/Models/
  AiModelContracts.cs
src/RouteTimer.Contracts/Predictions/
  prediction contract additions
src/RouteTimer.Client/Components/Ai/
  AiReadiness.razor
  PredictionAiSummary.razor
```

## Execution Order

| Task | Deliverable | Depends on |
|---|---|---|
| [01](01-ai-domain-and-readiness.md) | Stable AI domain types, evidence classification, readiness score and suggestions | weather plan complete |
| [02](02-training-history-freshness.md) | Persisted Garmin/manual history confirmation and freshness service | 01 |
| [03](03-feature-extraction-and-training-state.md) | Fixed route/state feature schemas with leakage guards | 01 |
| [04](04-historical-replay-and-effort-labels.md) | Shared deterministic model factory, historical replay, bounded effort solver | 03, weather predictor |
| [05](05-additive-regression-primitives.md) | Application-owned robust elastic-net and GAM artifacts/trainers/evaluator | 03 |
| [06](06-route-and-state-support-gates.md) | Robust scaling, five-neighbour route gate, state ranges, comparable errors | 03, 05 |
| [07](07-nested-chronological-evaluation.md) | Nested candidate selection, whole-ride scoring and publication decision | 04-06 |
| [08](08-ai-persistence-and-migrations.md) | Derived-example cache, immutable AI aggregates, validation and migrations | 01, 03, 05-07 |
| [09](09-ai-build-job-orchestration.md) | Derived-example source, coalesced build job and atomic challenger publication | 02, 04, 07, 08 |
| [10](10-prediction-mode-and-provenance-persistence.md) | Capture requested mode/model and persist backward-compatible AI provenance | 08, 09 |
| [11](11-ai-prediction-execution.md) | Route/state gates, physics rerun and complete fallback chain | 02, 05, 06, 08, 10 |
| [12](12-ai-status-and-history-api.md) | Nested model status, readiness/build state and manual confirmation endpoint | 02, 09 |
| [13](13-training-readiness-client.md) | Restrained readiness/freshness UI on Training, Home and Predictions | 12 |
| [14](14-prediction-mode-and-result-client.md) | Typical/Today choice and AI comparison/fallback result presentation | 10-12 |
| [15](15-rollout-operations-and-release-verification.md) | Shadow/comparison/automatic stages, telemetry, docs, adversarial and full regression | 01-14 |

Tasks are ordered commits, not parallel work. A fresh worker may execute one file after every preceding task commit is present.

## Review Checkpoints

Request code review after Tasks 04, 07, 11, and 15. These gates validate replay correctness, leakage-free publication, production fallback behaviour, and release readiness. Do not start the next checkpoint group until review findings are resolved, committed, and pushed.

## Spec Coverage Map

| Accepted spec area | Owning tasks |
|---|---|
| Prerequisite and weather isolation | 04, 07, 09, 15 |
| Readiness score, variety, suggestions | 01, 12, 13 |
| History freshness and Today fallback | 02, 03, 11-14 |
| Features and forbidden leakage | 03, 04, 07 |
| Effort-label solver and physics coherence | 04, 11 |
| Elastic-net/GAM artifacts | 05 |
| Route-specific and Today state gates | 06, 07, 11 |
| Nested chronological validation/publication gates | 07 |
| Immutable examples/models/prediction provenance | 08-10 |
| Build lifecycle and previous-model safety | 09 |
| Typical/Today serving and fallbacks | 10, 11, 14 |
| API/status surfaces | 12 |
| Restrained client language | 13, 14 |
| Local-only privacy, telemetry, rollout/rollback | 15 |
| Every acceptance criterion and full regression | 15 |

## Completion Definition

The series is complete only when Task 15's full .NET and JavaScript suites pass, the comparison and fallback fixtures have been manually inspected, `git status --short` is empty, all fifteen focused commits are on the feature branch's remote upstream, and the decision-bearing PR satisfies the repository narrative contract.

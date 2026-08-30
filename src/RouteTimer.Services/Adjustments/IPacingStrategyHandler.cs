using RouteTimer.Domain.Adjustments;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Predictions;
using RouteTimer.Domain.Profile;

namespace RouteTimer.Services.Adjustments;

public sealed record PacingStrategyContext(
    Guid BaselinePredictionId,
    PredictionRoute Route,
    PredictionResult Baseline,
    RiderProfile Profile,
    RiderModel Model);

public sealed record PacingStrategyComputation(
    PredictionResult Adjusted,
    PacingStrategyReport Report,
    IReadOnlyDictionary<int, PredictionAdjustmentAnnotation> Annotations,
    IReadOnlyList<string> Warnings,
    string AlgorithmVersion);

/// <summary>
/// One strategy's full vertical slice: it alone knows its concrete <see cref="PacingStrategyDefinition"/>
/// and <see cref="PacingStrategyReport"/> subtypes, so canonicalization and deserialization are the
/// handler's own responsibility rather than a central polymorphic root shared across strategies that
/// don't all exist yet. <see cref="PacingStrategyDispatcher"/> and the services that use it never need
/// to know a concrete type.
/// </summary>
public interface IPacingStrategyHandler
{
    PacingStrategyType Type { get; }
    string Canonicalize(PacingStrategyDefinition strategy);
    PacingStrategyDefinition Deserialize(string canonicalJson);
    string CanonicalizeReport(PacingStrategyReport report);
    PacingStrategyComputation Run(PacingStrategyContext context, PacingStrategyDefinition strategy, CancellationToken cancellationToken);
}

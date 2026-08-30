using RouteTimer.Contracts.Adjustments;
using RouteTimer.Domain.Adjustments.SegmentGains;

namespace RouteTimer.Api.Adjustments;

/// <summary>
/// A submitted segment-gains request failed structural validation. Field errors are keyed by
/// <c>rules[N]</c> (or <c>rules</c> for a definition-level failure such as exceeding the rule limit),
/// letting the client render each message next to the rule that produced it.
/// </summary>
public sealed class SegmentGainsRequestValidationException(IReadOnlyDictionary<string, string[]> errors)
    : Exception("The segment-specific gains request is invalid.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

/// <summary>
/// Maps the wire-format <see cref="SegmentSpecificGainsRequest"/> (which Contracts must keep dependency-free
/// of Domain) to its concrete <see cref="SegmentGainsDefinition"/>. Lives in Api, the only project allowed
/// to reference both Contracts and Domain.
/// </summary>
public static class SegmentGainsRequestMapper
{
    public static SegmentGainsDefinition ToDefinition(SegmentSpecificGainsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var rules = new List<SegmentGainsRule>(request.Rules.Count);
        for (var index = 0; index < request.Rules.Count; index++)
        {
            var ruleRequest = request.Rules[index];
            try
            {
                rules.Add(new SegmentGainsRule(
                    ruleRequest.MinGradient, ruleRequest.MaxGradient,
                    ruleRequest.MinSequence, ruleRequest.MaxSequence,
                    ruleRequest.MinCumulativeDistanceMetres, ruleRequest.MaxCumulativeDistanceMetres,
                    ruleRequest.Factor, ruleRequest.DeltaWatts));
            }
            catch (ArgumentException exception)
            {
                errors[$"rules[{index}]"] = [exception.Message];
            }
        }

        if (errors.Count > 0)
        {
            throw new SegmentGainsRequestValidationException(errors);
        }

        try
        {
            return new SegmentGainsDefinition(rules);
        }
        catch (ArgumentException exception)
        {
            throw new SegmentGainsRequestValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["rules"] = [exception.Message],
            });
        }
    }
}

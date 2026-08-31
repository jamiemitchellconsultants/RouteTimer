using System;
using System.Collections.Generic;
using RouteTimer.Contracts.Adjustments;
using RouteTimer.Domain.Adjustments.TimeTarget;

namespace RouteTimer.Api.Adjustments;

public sealed class TimeTargetRequestValidationException : Exception
{
    public TimeTargetRequestValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("Time target request validation failed.")
    {
        Errors = errors;
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}

public static class TimeTargetRequestMapper
{
    public static TimeTargetDefinition ToDefinition(TimeTargetRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var errors = new Dictionary<string, string[]>();

        TimeTargetDistribution distribution = TimeTargetDistribution.Proportional;
        if (string.Equals(request.Distribution, "proportional", StringComparison.OrdinalIgnoreCase))
        {
            distribution = TimeTargetDistribution.Proportional;
        }
        else if (string.Equals(request.Distribution, "climb-focused", StringComparison.OrdinalIgnoreCase))
        {
            distribution = TimeTargetDistribution.ClimbFocused;
        }
        else
        {
            errors["distribution"] = ["Distribution must be 'proportional' or 'climb-focused'."];
        }

        if (errors.Count > 0)
        {
            throw new TimeTargetRequestValidationException(errors);
        }

        try
        {
            return new TimeTargetDefinition(
                request.TargetMovingSeconds,
                distribution,
                request.ClimbBias,
                request.IncludeFeasibilityReport);
        }
        catch (ArgumentException ex)
        {
            if (ex.ParamName == "targetMovingSeconds")
            {
                errors["targetMovingSeconds"] = [ex.Message];
            }
            else if (ex.ParamName == "climbBias")
            {
                errors["climbBias"] = [ex.Message];
            }
            else
            {
                errors["distribution"] = [ex.Message];
            }

            throw new TimeTargetRequestValidationException(errors);
        }
    }
}

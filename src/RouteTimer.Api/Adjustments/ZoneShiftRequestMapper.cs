using System;
using System.Collections.Generic;
using RouteTimer.Contracts.Adjustments;
using RouteTimer.Domain.Adjustments.Zones;

namespace RouteTimer.Api.Adjustments;

public sealed class ZoneShiftRequestValidationException : Exception
{
    public ZoneShiftRequestValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("Zone shift request validation failed.")
    {
        Errors = errors;
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}

public static class ZoneShiftRequestMapper
{
    public static ZoneShiftDefinition ToDefinition(RpeZoneShiftRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var errors = new Dictionary<string, string[]>();

        ZoneThresholdMode mode = ZoneThresholdMode.FtpBased;
        if (string.Equals(request.ThresholdMode, "ftp-based", StringComparison.OrdinalIgnoreCase))
        {
            mode = ZoneThresholdMode.FtpBased;
        }
        else if (string.Equals(request.ThresholdMode, "model-inferred", StringComparison.OrdinalIgnoreCase))
        {
            mode = ZoneThresholdMode.ModelInferred;
        }
        else
        {
            errors["thresholdMode"] = ["Threshold mode must be 'ftp-based' or 'model-inferred'."];
        }

        var domainAssignments = new List<ZoneAssignment>();
        if (request.Assignments is null || request.Assignments.Count == 0)
        {
            errors["assignments"] = ["Assignments collection cannot be empty."];
        }
        else
        {
            for (int i = 0; i < request.Assignments.Count; i++)
            {
                var reqAssign = request.Assignments[i];
                if (reqAssign is null)
                {
                    errors[$"assignments[{i}]"] = ["Assignment cannot be null."];
                    continue;
                }

                ZonePlacement placement = ZonePlacement.Midpoint;
                if (string.Equals(reqAssign.Placement, "lower-bound", StringComparison.OrdinalIgnoreCase))
                    placement = ZonePlacement.LowerBound;
                else if (string.Equals(reqAssign.Placement, "midpoint", StringComparison.OrdinalIgnoreCase))
                    placement = ZonePlacement.Midpoint;
                else if (string.Equals(reqAssign.Placement, "upper-bound", StringComparison.OrdinalIgnoreCase))
                    placement = ZonePlacement.UpperBound;
                else
                    errors[$"assignments[{i}].placement"] = ["Placement must be 'lower-bound', 'midpoint', or 'upper-bound'."];

                bool allSegments = reqAssign.AllSegments;

                try
                {
                    domainAssignments.Add(new ZoneAssignment(
                        allSegments,
                        allSegments ? null : reqAssign.MinGradient,
                        allSegments ? null : reqAssign.MaxGradient,
                        reqAssign.Zone,
                        placement));
                }
                catch (ArgumentException ex)
                {
                    errors[$"assignments[{i}]"] = [ex.Message];
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new ZoneShiftRequestValidationException(errors);
        }

        try
        {
            return new ZoneShiftDefinition(mode, request.FtpWatts, domainAssignments);
        }
        catch (ArgumentException ex)
        {
            if (ex.ParamName == "ftpWatts")
            {
                errors["ftpWatts"] = [ex.Message];
            }
            else
            {
                errors["assignments"] = [ex.Message];
            }

            throw new ZoneShiftRequestValidationException(errors);
        }
    }
}

using System;
using System.Collections.Generic;
using RouteTimer.Contracts.Adjustments;
using RouteTimer.Domain.Adjustments.NpIf;

namespace RouteTimer.Api.Adjustments;

public sealed class NpIfRequestValidationException : Exception
{
    public NpIfRequestValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("NP/IF request validation failed.")
    {
        Errors = errors;
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}

public static class NpIfRequestMapper
{
    public static NpIfTargetDefinition ToDefinition(NpIfTargetRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var errors = new Dictionary<string, string[]>();

        NpIfScalingMode mode = NpIfScalingMode.Proportional;
        if (string.Equals(request.Mode, "proportional", StringComparison.OrdinalIgnoreCase))
        {
            mode = NpIfScalingMode.Proportional;
        }
        else if (string.Equals(request.Mode, "additive", StringComparison.OrdinalIgnoreCase))
        {
            mode = NpIfScalingMode.Additive;
        }
        else
        {
            errors["mode"] = ["Mode must be 'proportional' or 'additive'."];
        }

        if (errors.Count > 0)
        {
            throw new NpIfRequestValidationException(errors);
        }

        try
        {
            return new NpIfTargetDefinition(
                request.TargetIntensityFactor,
                request.FtpWatts,
                mode);
        }
        catch (ArgumentException ex)
        {
            if (ex.ParamName == "targetIntensityFactor")
            {
                errors["targetIntensityFactor"] = [ex.Message];
            }
            else if (ex.ParamName == "ftpWatts")
            {
                errors["ftpWatts"] = [ex.Message];
            }
            else
            {
                errors["mode"] = [ex.Message];
            }

            throw new NpIfRequestValidationException(errors);
        }
    }
}

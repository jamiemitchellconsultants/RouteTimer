using System;
using System.Collections.Generic;
using RouteTimer.Contracts.Adjustments;
using RouteTimer.Domain.Adjustments.MatchBurning;

namespace RouteTimer.Api.Adjustments;

public sealed class MatchBurningRequestValidationException : Exception
{
    public MatchBurningRequestValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("Match burning request validation failed.")
    {
        Errors = errors;
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}

public static class MatchBurningRequestMapper
{
    public static MatchBurningDefinition ToDefinition(VariableMatchBurningRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var errors = new Dictionary<string, string[]>();

        var domainWindows = new List<MatchBurnWindow>();
        if (request.Windows is null || request.Windows.Count == 0)
        {
            errors["windows"] = ["Windows collection cannot be empty."];
        }
        else
        {
            for (int i = 0; i < request.Windows.Count; i++)
            {
                var reqWin = request.Windows[i];
                if (reqWin is null)
                {
                    errors[$"windows[{i}]"] = ["Window cannot be null."];
                    continue;
                }

                try
                {
                    domainWindows.Add(new MatchBurnWindow(
                        string.Equals(reqWin.Selector, "gradient", StringComparison.OrdinalIgnoreCase) ? reqWin.MinGradient : null,
                        string.Equals(reqWin.Selector, "gradient", StringComparison.OrdinalIgnoreCase) ? reqWin.MaxGradient : null,
                        string.Equals(reqWin.Selector, "distance", StringComparison.OrdinalIgnoreCase) ? reqWin.MinDistanceMetres : null,
                        string.Equals(reqWin.Selector, "distance", StringComparison.OrdinalIgnoreCase) ? reqWin.MaxDistanceMetres : null,
                        string.Equals(reqWin.Selector, "sequence", StringComparison.OrdinalIgnoreCase) ? reqWin.MinSequence : null,
                        string.Equals(reqWin.Selector, "sequence", StringComparison.OrdinalIgnoreCase) ? reqWin.MaxSequence : null,
                        string.Equals(reqWin.Intensity, "absolute-watts", StringComparison.OrdinalIgnoreCase) ? reqWin.AbsoluteWatts : null,
                        string.Equals(reqWin.Intensity, "percent-cp", StringComparison.OrdinalIgnoreCase) ? reqWin.PercentCp : null,
                        string.Equals(reqWin.Intensity, "cp-zone", StringComparison.OrdinalIgnoreCase) ? reqWin.CpZone : null));
                }
                catch (ArgumentException ex)
                {
                    errors[$"windows[{i}]"] = [ex.Message];
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new MatchBurningRequestValidationException(errors);
        }

        try
        {
            return new MatchBurningDefinition(
                request.CriticalPowerWatts,
                request.WPrimeJoules,
                domainWindows,
                request.ConservationDurationSeconds,
                request.ConservationTargetCpFraction,
                request.RecoveryDurationSeconds,
                request.RecoveryTargetCpFraction,
                request.IncludeFatigueReport,
                request.EnableRefinement);
        }
        catch (ArgumentException ex)
        {
            if (ex.ParamName == "criticalPowerWatts")
            {
                errors["criticalPowerWatts"] = [ex.Message];
            }
            else if (ex.ParamName == "wPrimeJoules")
            {
                errors["wPrimeJoules"] = [ex.Message];
            }
            else if (ex.ParamName == "conservationDurationSeconds" || ex.ParamName == "conservationTargetCpFraction")
            {
                errors["conservation"] = [ex.Message];
            }
            else if (ex.ParamName == "recoveryDurationSeconds" || ex.ParamName == "recoveryTargetCpFraction")
            {
                errors["recovery"] = [ex.Message];
            }
            else
            {
                errors["windows"] = [ex.Message];
            }

            throw new MatchBurningRequestValidationException(errors);
        }
    }
}

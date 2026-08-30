using System;
using System.Collections.Generic;
using System.Linq;
using RouteTimer.Domain.Adjustments;
using RouteTimer.Domain.Adjustments.MatchBurning;
using RouteTimer.Domain.Models;

namespace RouteTimer.Services.Adjustments.MatchBurning;

public sealed record ResolvedMatchCapacity(
    double CriticalPowerWatts,
    CapacityProvenance CriticalPowerProvenance,
    double WPrimeJoules,
    CapacityProvenance WPrimeProvenance,
    IReadOnlyList<string> Warnings);

public static class CapacityResolver
{
    public static ResolvedMatchCapacity Resolve(MatchBurningDefinition definition, PowerModel model)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));
        if (model is null) throw new ArgumentNullException(nameof(model));

        var warnings = new List<string>();

        double cpWatts;
        CapacityProvenance cpProvenance;

        if (definition.CriticalPowerWatts is not null)
        {
            cpWatts = definition.CriticalPowerWatts.Value;
            cpProvenance = CapacityProvenance.Supplied;
        }
        else
        {
            var longFlatBand = model.Bands.FirstOrDefault(b => b.GradeKey == "-1:1" && b.DurationKey == "180:+");
            if (longFlatBand is not null && double.IsFinite(longFlatBand.TypicalWatts) && longFlatBand.TypicalWatts > 0)
            {
                cpWatts = 0.95 * longFlatBand.TypicalWatts;
                cpProvenance = CapacityProvenance.InferredModel;
            }
            else
            {
                cpWatts = 0.95 * model.GlobalTypicalWatts;
                cpProvenance = CapacityProvenance.Fallback;
                warnings.Add(AdjustmentWarningCodes.MatchBurningCpLowConfidence);

                if (double.IsNaN(cpWatts) || double.IsInfinity(cpWatts) || cpWatts <= 0)
                {
                    throw new ArgumentException("Inferred critical power watts must be positive.");
                }
            }
        }

        double wPrimeJoules;
        CapacityProvenance wPrimeProvenance;

        if (definition.WPrimeJoules is not null)
        {
            wPrimeJoules = definition.WPrimeJoules.Value;
            wPrimeProvenance = CapacityProvenance.Supplied;
        }
        else
        {
            var shortClimbBand = model.Bands.FirstOrDefault(b => b.GradeKey == "1:3" && b.DurationKey == "0:30");
            if (shortClimbBand is not null && double.IsFinite(shortClimbBand.TypicalWatts) && shortClimbBand.TypicalWatts > cpWatts)
            {
                double calculatedJoules = (shortClimbBand.TypicalWatts - cpWatts) * 900.0;
                if (double.IsFinite(calculatedJoules) && calculatedJoules > 0)
                {
                    wPrimeJoules = Math.Clamp(calculatedJoules, 1000.0, 100000.0);
                    wPrimeProvenance = CapacityProvenance.InferredModel;
                }
                else
                {
                    wPrimeJoules = 15000.0;
                    wPrimeProvenance = CapacityProvenance.Fallback;
                    warnings.Add(AdjustmentWarningCodes.MatchBurningWPrimeInferredDefault);
                }
            }
            else
            {
                wPrimeJoules = 15000.0;
                wPrimeProvenance = CapacityProvenance.Fallback;
                warnings.Add(AdjustmentWarningCodes.MatchBurningWPrimeInferredDefault);
            }
        }

        return new ResolvedMatchCapacity(cpWatts, cpProvenance, wPrimeJoules, wPrimeProvenance, warnings);
    }
}

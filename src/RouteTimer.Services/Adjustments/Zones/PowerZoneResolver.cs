using System;
using System.Collections.Generic;
using RouteTimer.Domain.Adjustments.Zones;
using RouteTimer.Domain.Models;

namespace RouteTimer.Services.Adjustments.Zones;

public static class PowerZoneResolver
{
    public static ResolvedPowerZoneSet Resolve(
        ZoneThresholdMode mode,
        double? ftpWatts,
        RiderModel model)
    {
        if (model is null) throw new ArgumentNullException(nameof(model));

        double thresholdWatts;
        ZoneThresholdProvenance provenance;

        if (mode == ZoneThresholdMode.FtpBased)
        {
            if (ftpWatts is null || ftpWatts.Value <= 0)
            {
                throw new ArgumentException("FTP watts must be positive in FtpBased mode.", nameof(ftpWatts));
            }
            thresholdWatts = ftpWatts.Value;
            provenance = ZoneThresholdProvenance.SuppliedFtp;
        }
        else
        {
            thresholdWatts = model.PowerModel.GlobalTypicalWatts / 0.83;
            if (double.IsNaN(thresholdWatts) || double.IsInfinity(thresholdWatts) || thresholdWatts <= 0)
            {
                throw new ArgumentException("Inferred threshold watts from rider model must be positive.");
            }
            provenance = ZoneThresholdProvenance.InferredModel;
        }

        var zones = new List<ResolvedPowerZone>();
        int zoneCount = mode == ZoneThresholdMode.FtpBased ? 7 : 5;

        for (int z = 1; z <= zoneCount; z++)
        {
            double lowFrac, highFrac;
            switch (z)
            {
                case 1: lowFrac = 0.0; highFrac = 0.55; break;
                case 2: lowFrac = 0.55; highFrac = 0.75; break;
                case 3: lowFrac = 0.75; highFrac = 0.90; break;
                case 4: lowFrac = 0.90; highFrac = 1.05; break;
                case 5: lowFrac = 1.05; highFrac = mode == ZoneThresholdMode.FtpBased ? 1.20 : 1.50; break;
                case 6: lowFrac = 1.20; highFrac = 1.50; break;
                case 7: lowFrac = 1.50; highFrac = 2.00; break;
                default: throw new InvalidOperationException($"Invalid zone index {z}");
            }

            double lowerWatts = thresholdWatts * lowFrac;
            double upperWatts = thresholdWatts * highFrac;

            double lowerTarget, midTarget, upperTarget;

            if (z == 7)
            {
                lowerTarget = thresholdWatts * 1.51;
                midTarget = thresholdWatts * 1.60;
                upperTarget = thresholdWatts * 2.00;
            }
            else
            {
                double range = upperWatts - lowerWatts;
                if (range < 10.0)
                {
                    lowerTarget = (lowerWatts + upperWatts) / 2.0;
                    midTarget = lowerTarget;
                    upperTarget = lowerTarget;
                }
                else
                {
                    lowerTarget = z == 1 ? 5.0 : lowerWatts + 5.0;
                    upperTarget = upperWatts - 5.0;
                    midTarget = (lowerWatts + upperWatts) / 2.0;
                }
            }

            zones.Add(new ResolvedPowerZone(z, lowerWatts, upperWatts, lowerTarget, midTarget, upperTarget));
        }

        return new ResolvedPowerZoneSet(thresholdWatts, provenance, zones);
    }

    public static double SelectTarget(ResolvedPowerZone zone, ZonePlacement placement) => placement switch
    {
        ZonePlacement.LowerBound => zone.LowerTargetWatts,
        ZonePlacement.Midpoint => zone.MidpointTargetWatts,
        ZonePlacement.UpperBound => zone.UpperTargetWatts,
        _ => throw new ArgumentException($"Unhandled placement {placement}", nameof(placement)),
    };
}

using RouteTimer.Domain.Activities;
using RouteTimer.Domain.Routes;
using RouteTimer.Services.Routes;

namespace RouteTimer.Services.Activities;

public interface ITrainingGeometryEnricher
{
    CleanedActivity Enrich(CleanedActivity activity);
}

public sealed class TrainingGeometryEnricher(RouteProcessingOptions routeOptions) : ITrainingGeometryEnricher
{
    public CleanedActivity Enrich(CleanedActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var enriched = new List<CleanRideSample>(activity.Samples.Count);
        var section = new List<CleanRideSample>();
        foreach (var sample in activity.Samples)
        {
            if (sample.CrossesDiscontinuity && section.Count > 0)
            {
                EnrichSection(section, enriched);
                section.Clear();
            }

            section.Add(sample);
        }

        if (section.Count > 0)
        {
            EnrichSection(section, enriched);
        }

        return activity with { Samples = enriched };
    }

    private void EnrichSection(IReadOnlyList<CleanRideSample> section, ICollection<CleanRideSample> enriched)
    {
        var points = section.Select(sample => sample.Position).ToArray();
        var distances = RouteGeometry.CumulativeDistances(points);
        var geometry = RouteGeometry.Enrich(points, distances, routeOptions.ElevationWindowMetres);
        for (var index = 0; index < section.Count; index++)
        {
            var sample = section[index];
            var value = geometry[index];
            enriched.Add(sample with
            {
                Gradient = value.Gradient,
                CurvaturePerMetre = value.CurvaturePerMetre
            });
        }
    }
}

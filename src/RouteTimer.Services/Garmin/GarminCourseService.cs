using RouteTimer.Services.Persistence;
using RouteTimer.Services.Routes;

namespace RouteTimer.Services.Garmin;

public sealed record GarminCourseOptions(string? Name, string? ActivityType);

public sealed record GarminCourseCreation(long CourseId, string CourseName, string CourseUrl);

public sealed class PredictionMissingException() : Exception("The prediction was not found.");

public sealed class GarminCourseService(
    IGarminAdapterClient adapter,
    IGarminConnectionRepository connections,
    IPredictionRepository predictions,
    IGarminTokenProtector protector,
    GarminOperationGate gate,
    TimeProvider timeProvider)
{
    private const string DefaultActivityType = "road_biking";

    public Task<GarminCourseCreation> CreateCourseAsync(
        Guid predictionId,
        GarminCourseOptions options,
        CancellationToken cancellationToken) =>
        // The gate is why this is not a plain call: a course push must not interleave with an
        // activity import or a session validation, because all three share one Garmin session and
        // each one can rotate its tokens.
        gate.RunAsync(async token =>
        {
            // Checked before the connection: a prediction that doesn't exist is worth reporting
            // as such even when Garmin isn't connected, rather than sending the rider to reconnect
            // Garmin for a request that was never going to succeed.
            var source = await predictions.GetGpxSourceAsync(predictionId, token)
                ?? throw new PredictionMissingException();

            var connection = await connections.GetAsync(token)
                ?? throw new GarminConnectionRequiredException();
            if (connection.State == "reconnect-required")
            {
                throw new GarminReconnectRequiredException();
            }

            // Always the untimed variant, whatever the rider last downloaded: a timestamped track
            // is what makes some importers treat a course as an activity.
            var gpx = PredictionGpxWriter.Write(source, timed: false);
            var fileName = PredictionGpxWriter.SuggestFileName(source.RouteName);
            var (gain, loss) = ElevationTotals(source);

            var created = await adapter.CreateCourseAsync(
                protector.Unprotect(connection.Token),
                new GarminCourseRequest(
                    fileName,
                    options.Name ?? source.RouteName,
                    options.ActivityType ?? DefaultActivityType,
                    source.Description,
                    gain,
                    loss,
                    System.Text.Encoding.UTF8.GetBytes(gpx)),
                token);

            var now = timeProvider.GetUtcNow();
            await connections.SaveAsync(
                connection with
                {
                    Token = protector.Protect(created.TokenJson),
                    LastValidatedAt = now,
                    UpdatedAt = now
                },
                token);
            await predictions.RecordGarminCourseAsync(predictionId, created.CourseId, now, token);

            return new GarminCourseCreation(
                created.CourseId,
                created.CourseName,
                $"https://connect.garmin.com/modern/course/{created.CourseId}");
        }, cancellationToken);

    private static (double Gain, double Loss) ElevationTotals(PredictionGpxSource source)
    {
        double gain = 0, loss = 0;
        var ordered = source.Segments.OrderBy(segment => segment.Sequence).ToList();
        for (var index = 1; index < ordered.Count; index++)
        {
            var delta = ordered[index].ElevationMetres - ordered[index - 1].ElevationMetres;
            if (delta > 0)
            {
                gain += delta;
            }
            else
            {
                loss -= delta;
            }
        }

        return (gain, loss);
    }
}

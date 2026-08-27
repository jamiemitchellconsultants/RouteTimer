using System.Text;
using RouteTimer.Services.Predictions;
using RouteTimer.Services.Routes;

namespace RouteTimer.Services.RoutePacer;

public sealed record RoutePacerHandoffConfiguration(bool Enabled, Uri RoutePacerBaseUrl);

public sealed record RoutePacerHandoff(Uri Url, DateTimeOffset ExpiresAt);

public sealed class RoutePacerHandoffDisabledException() : Exception("The RoutePacer handoff is disabled.");

public sealed class RoutePacerPredictionMissingException() : Exception("The prediction was not found.");

public sealed class RoutePacerHandoffService(
    PredictionQueryService predictions,
    IRoutePacerRelayClient relay,
    IRoutePacerInvocationSigner signer,
    RoutePacerHandoffConfiguration configuration,
    TimeProvider timeProvider)
{
    public async Task<RoutePacerHandoff> CreateAsync(Guid predictionId, CancellationToken cancellationToken)
    {
        // Checked before anything else, so a disabled deployment never reads a prediction, never
        // opens an outbound connection, and never reaches the signer it has no key for.
        if (!configuration.Enabled)
        {
            throw new RoutePacerHandoffDisabledException();
        }

        var source = await predictions.GetGpxSourceAsync(predictionId, cancellationToken)
            ?? throw new RoutePacerPredictionMissingException();

        // Always the timed variant: pacing is the entire point of the handoff, and this is the
        // same writer call the timed download uses, so the phone gets byte-identical content.
        // Write throws PredictionNotCompleteException for a segment-free prediction, which is the
        // existing incomplete signal the endpoint already maps to 409 -- deliberately not caught.
        var gpx = Encoding.UTF8.GetBytes(PredictionGpxWriter.Write(source, timed: true));

        var grant = await relay.UploadAsync(gpx, cancellationToken);

        // Signed only after the relay grant has been validated: signing an unvalidated payload URL
        // would put RouteTimer's own signature on a link to wherever the response pointed.
        var url = RoutePacerContract.BuildInvocationUrl(
            configuration.RoutePacerBaseUrl,
            grant.PayloadUrl,
            source.RouteName,
            timeProvider.GetUtcNow(),
            signer);

        return new RoutePacerHandoff(url, grant.ExpiresAt);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using RouteTimer.Services.RoutePacer;

namespace RouteTimer.Api.RoutePacer;

public sealed class RoutePacerRelayClient(
    HttpClient httpClient,
    RoutePacerHandoffOptions options,
    TimeProvider timeProvider) : IRoutePacerRelayClient
{
    private const string UploadPath = "/api/handoffs";
    private const string PayloadPathPrefix = "/api/handoffs/";
    private const int TokenLength = 43;

    // The relay fixes the lifetime at ten minutes. Thirty seconds of slack absorbs ordinary clock
    // skew between the two hosts; anything beyond that is a relay this client should not trust,
    // because the QR would then promise the rider more time than the payload actually has.
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record RelayGrantBody(
        [property: JsonPropertyName("payloadUrl")] string? PayloadUrl,
        [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt);

    public async Task<RoutePacerRelayGrant> UploadAsync(byte[] timedGpx, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timedGpx);

        using var request = new HttpRequestMessage(HttpMethod.Post, UploadPath)
        {
            Content = new ByteArrayContent(timedGpx)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/gpx+xml");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        // Set per request rather than as a client default: a default header would ride along on
        // any other call that ever shares this typed client.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.RelayUploadKey);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The rider navigated away or the request was aborted. Not a relay failure, and
            // reporting it as one would show a misleading error on a page nobody is watching.
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            throw Unavailable(exception);
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.Created)
            {
                throw MapFailure(response);
            }

            RelayGrantBody? body;
            try
            {
                await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
                body = await JsonSerializer.DeserializeAsync<RelayGrantBody>(content, JsonOptions, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is JsonException or HttpRequestException or IOException)
            {
                throw InvalidResponse(exception);
            }

            return Validate(body);
        }
    }

    private RoutePacerRelayGrant Validate(RelayGrantBody? body)
    {
        if (body?.PayloadUrl is null || body.ExpiresAt is not { } expiresAt)
        {
            throw InvalidResponse();
        }

        if (!Uri.TryCreate(body.PayloadUrl, UriKind.Absolute, out var payloadUrl)
            || payloadUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw InvalidResponse();
        }

        // Origin equality, not a suffix or host check: a payload URL on any other host is either a
        // relay misconfiguration or an attempt to point the rider's phone somewhere else, and the
        // signature would make either one look authentic.
        var configuredOrigin = new Uri(options.RoutePacerBaseUrl, UriKind.Absolute);
        if (!string.Equals(
                payloadUrl.GetLeftPart(UriPartial.Authority),
                configuredOrigin.GetLeftPart(UriPartial.Authority),
                StringComparison.Ordinal))
        {
            throw InvalidResponse();
        }

        if (payloadUrl.Query.Length > 0 || payloadUrl.Fragment.Length > 0)
        {
            throw InvalidResponse();
        }

        if (!payloadUrl.AbsolutePath.StartsWith(PayloadPathPrefix, StringComparison.Ordinal))
        {
            throw InvalidResponse();
        }

        var token = payloadUrl.AbsolutePath[PayloadPathPrefix.Length..];
        if (token.Length != TokenLength || !token.All(IsBase64UrlCharacter))
        {
            throw InvalidResponse();
        }

        var now = timeProvider.GetUtcNow();
        if (expiresAt <= now || expiresAt - now > MaximumLifetime)
        {
            throw InvalidResponse();
        }

        return new RoutePacerRelayGrant(payloadUrl, expiresAt);
    }

    private static bool IsBase64UrlCharacter(char value) =>
        char.IsAsciiLetterOrDigit(value) || value == '-' || value == '_';

    private static RoutePacerRelayException MapFailure(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new RoutePacerRelayException(
            RoutePacerRelayFailure.Authentication,
            "The RoutePacer relay rejected the configured upload credential."),
        HttpStatusCode.RequestEntityTooLarge => new RoutePacerRelayException(
            RoutePacerRelayFailure.PayloadTooLarge,
            "The route is too large for the RoutePacer relay."),
        HttpStatusCode.UnsupportedMediaType or HttpStatusCode.BadRequest => new RoutePacerRelayException(
            RoutePacerRelayFailure.RejectedPayload,
            "The RoutePacer relay rejected the uploaded route."),
        HttpStatusCode.TooManyRequests => new RoutePacerRelayException(
            RoutePacerRelayFailure.RateLimited,
            "The RoutePacer relay is rate limiting uploads.",
            RetryAfter(response)),
        >= HttpStatusCode.InternalServerError => new RoutePacerRelayException(
            RoutePacerRelayFailure.Unavailable,
            "The RoutePacer relay is unavailable."),
        // Everything else, including a redirect this client deliberately did not follow: the relay
        // did not answer with the frozen 201 contract, so the response cannot be trusted.
        _ => InvalidResponse()
    };

    // Only a whole-second delta is accepted. An HTTP-date form would need the relay's clock to
    // agree with ours, which is exactly the assumption the expiry validation above refuses to make.
    private static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero
            ? delta
            : null;

    private static RoutePacerRelayException InvalidResponse(Exception? inner = null) => new(
        RoutePacerRelayFailure.InvalidResponse,
        "The RoutePacer relay returned a response that does not match the handoff contract.",
        innerException: inner);

    private static RoutePacerRelayException Unavailable(Exception inner) => new(
        RoutePacerRelayFailure.Unavailable,
        "The RoutePacer relay could not be reached.",
        innerException: inner);
}

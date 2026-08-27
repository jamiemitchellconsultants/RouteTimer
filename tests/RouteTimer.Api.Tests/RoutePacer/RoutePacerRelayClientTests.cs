using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using RouteTimer.Api.RoutePacer;
using RouteTimer.Services.RoutePacer;

namespace RouteTimer.Api.Tests.RoutePacer;

public sealed class RoutePacerRelayClientTests
{
    private const string Origin = RoutePacerHandoffOptionsTests.TestRelayOrigin;
    private const string Token = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-27T12:00:00Z");
    private static readonly byte[] Gpx = Encoding.UTF8.GetBytes("<gpx><trk/></gpx>");

    [Fact]
    public async Task Upload_posts_the_exact_bytes_with_the_frozen_relay_contract()
    {
        HttpRequestMessage? seen = null;
        byte[]? body = null;
        var client = Client(async (request, cancellationToken) =>
        {
            seen = request;
            body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return Created();
        });

        await client.UploadAsync(Gpx, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, seen!.Method);
        Assert.Equal($"{Origin}/api/handoffs", seen.RequestUri!.AbsoluteUri);
        Assert.Equal("application/gpx+xml", seen.Content!.Headers.ContentType!.MediaType);
        Assert.True(seen.Headers.CacheControl!.NoStore);
        Assert.Equal("Bearer", seen.Headers.Authorization!.Scheme);
        Assert.Equal("test-upload-key", seen.Headers.Authorization.Parameter);
        Assert.Equal(Gpx, body);
    }

    // The credential must be attached per request rather than as a client default header, so it
    // cannot ride along on any other call that happens to share the typed client.
    [Fact]
    public async Task Upload_does_not_leave_the_credential_on_the_shared_client()
    {
        HttpClient? shared = null;
        var client = Client((_, _) => Task.FromResult(Created()), captured => shared = captured);

        await client.UploadAsync(Gpx, CancellationToken.None);

        Assert.Null(shared!.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task Upload_returns_the_validated_grant()
    {
        var client = Client((_, _) => Task.FromResult(Created(expiresAt: Now.AddMinutes(10))));

        var grant = await client.UploadAsync(Gpx, CancellationToken.None);

        Assert.Equal(new Uri($"{Origin}/api/handoffs/{Token}"), grant.PayloadUrl);
        Assert.Equal(Now.AddMinutes(10), grant.ExpiresAt);
    }

    // Headers are read before the body: the relay's response is small, but the client must never
    // buffer an arbitrarily large body from a host it is about to reject.
    [Fact]
    public async Task Upload_reads_response_headers_before_the_body()
    {
        var content = new ReadRecordingContent("{\"payloadUrl\":\"" + Origin + "/api/handoffs/" + Token + "\",\"expiresAt\":\"2026-08-27T12:10:00Z\"}");
        var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = content }));

        await client.UploadAsync(Gpx, CancellationToken.None);

        Assert.True(content.WasRead);
    }

    // A redirect the client followed would put the bearer upload credential on another host.
    [Fact]
    public async Task Upload_treats_a_redirect_as_an_invalid_response()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri("https://elsewhere.invalid/api/handoffs");
        var client = Client((_, _) => Task.FromResult(response));

        var failure = await Assert.ThrowsAsync<RoutePacerRelayException>(
            () => client.UploadAsync(Gpx, CancellationToken.None));

        Assert.Equal(RoutePacerRelayFailure.InvalidResponse, failure.Failure);
    }

    [Theory]
    // Not HTTPS.
    [InlineData("http://pacetracking.tqaentry.com/api/handoffs/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    // Foreign origin, even over HTTPS.
    [InlineData("https://elsewhere.invalid/api/handoffs/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    // Wrong path.
    [InlineData("https://pacetracking.tqaentry.com/api/other/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    // Token too short.
    [InlineData("https://pacetracking.tqaentry.com/api/handoffs/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    // Token too long.
    [InlineData("https://pacetracking.tqaentry.com/api/handoffs/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    // Token outside the base64url alphabet.
    [InlineData("https://pacetracking.tqaentry.com/api/handoffs/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    // Query and fragment are not part of the contract.
    [InlineData("https://pacetracking.tqaentry.com/api/handoffs/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA?a=b")]
    [InlineData("https://pacetracking.tqaentry.com/api/handoffs/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA#top")]
    [InlineData("/api/handoffs/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Upload_rejects_a_payload_url_outside_the_contract(string payloadUrl)
    {
        var client = Client((_, _) => Task.FromResult(Created(payloadUrl: payloadUrl)));

        var failure = await Assert.ThrowsAsync<RoutePacerRelayException>(
            () => client.UploadAsync(Gpx, CancellationToken.None));

        Assert.Equal(RoutePacerRelayFailure.InvalidResponse, failure.Failure);
    }

    [Theory]
    // Already expired on arrival.
    [InlineData(-1)]
    [InlineData(0)]
    // Beyond the ten-minute lifetime plus the thirty seconds of clock-skew tolerance.
    [InlineData(631)]
    public async Task Upload_rejects_an_expiry_outside_the_fixed_lifetime(int secondsFromNow)
    {
        var client = Client((_, _) => Task.FromResult(Created(expiresAt: Now.AddSeconds(secondsFromNow))));

        var failure = await Assert.ThrowsAsync<RoutePacerRelayException>(
            () => client.UploadAsync(Gpx, CancellationToken.None));

        Assert.Equal(RoutePacerRelayFailure.InvalidResponse, failure.Failure);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(600)]
    [InlineData(630)]
    public async Task Upload_accepts_an_expiry_inside_the_fixed_lifetime(int secondsFromNow)
    {
        var client = Client((_, _) => Task.FromResult(Created(expiresAt: Now.AddSeconds(secondsFromNow))));

        var grant = await client.UploadAsync(Gpx, CancellationToken.None);

        Assert.Equal(Now.AddSeconds(secondsFromNow), grant.ExpiresAt);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, RoutePacerRelayFailure.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, RoutePacerRelayFailure.Authentication)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, RoutePacerRelayFailure.PayloadTooLarge)]
    [InlineData(HttpStatusCode.UnsupportedMediaType, RoutePacerRelayFailure.RejectedPayload)]
    [InlineData(HttpStatusCode.BadRequest, RoutePacerRelayFailure.RejectedPayload)]
    [InlineData(HttpStatusCode.InternalServerError, RoutePacerRelayFailure.Unavailable)]
    [InlineData(HttpStatusCode.BadGateway, RoutePacerRelayFailure.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, RoutePacerRelayFailure.Unavailable)]
    [InlineData(HttpStatusCode.OK, RoutePacerRelayFailure.InvalidResponse)]
    public async Task Upload_maps_relay_status_codes_to_typed_failures(HttpStatusCode status, RoutePacerRelayFailure expected)
    {
        var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("relay said something private")
        }));

        var failure = await Assert.ThrowsAsync<RoutePacerRelayException>(
            () => client.UploadAsync(Gpx, CancellationToken.None));

        Assert.Equal(expected, failure.Failure);
        Assert.DoesNotContain("relay said something private", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("test-upload-key", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Origin, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rate_limited_upload_carries_a_valid_retry_after()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
        var client = Client((_, _) => Task.FromResult(response));

        var failure = await Assert.ThrowsAsync<RoutePacerRelayException>(
            () => client.UploadAsync(Gpx, CancellationToken.None));

        Assert.Equal(RoutePacerRelayFailure.RateLimited, failure.Failure);
        Assert.Equal(TimeSpan.FromSeconds(45), failure.RetryAfter);
    }

    [Fact]
    public async Task Rate_limited_upload_without_a_usable_retry_after_reports_none()
    {
        var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));

        var failure = await Assert.ThrowsAsync<RoutePacerRelayException>(
            () => client.UploadAsync(Gpx, CancellationToken.None));

        Assert.Equal(RoutePacerRelayFailure.RateLimited, failure.Failure);
        Assert.Null(failure.RetryAfter);
    }

    [Fact]
    public async Task Upload_maps_a_timeout_to_unavailable()
    {
        var client = Client((_, _) => throw new TaskCanceledException("timed out", new TimeoutException()));

        var failure = await Assert.ThrowsAsync<RoutePacerRelayException>(
            () => client.UploadAsync(Gpx, CancellationToken.None));

        Assert.Equal(RoutePacerRelayFailure.Unavailable, failure.Failure);
    }

    [Fact]
    public async Task Upload_maps_a_network_failure_to_unavailable()
    {
        var client = Client((_, _) => throw new HttpRequestException("dns is down for pacetracking.tqaentry.com"));

        var failure = await Assert.ThrowsAsync<RoutePacerRelayException>(
            () => client.UploadAsync(Gpx, CancellationToken.None));

        Assert.Equal(RoutePacerRelayFailure.Unavailable, failure.Failure);
        Assert.DoesNotContain("dns is down", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"expiresAt\":\"2026-08-27T12:10:00Z\"}")]
    [InlineData("{\"payloadUrl\":\"https://pacetracking.tqaentry.com/api/handoffs/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}")]
    public async Task Upload_maps_a_malformed_success_body_to_invalid_response(string body)
    {
        var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));

        var failure = await Assert.ThrowsAsync<RoutePacerRelayException>(
            () => client.UploadAsync(Gpx, CancellationToken.None));

        Assert.Equal(RoutePacerRelayFailure.InvalidResponse, failure.Failure);
    }

    [Fact]
    public async Task Upload_propagates_caller_cancellation_rather_than_reporting_a_relay_failure()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var client = Client((_, token) => throw new TaskCanceledException("cancelled", null, token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.UploadAsync(Gpx, cancellation.Token));
    }

    private static HttpResponseMessage Created(string? payloadUrl = null, DateTimeOffset? expiresAt = null) =>
        new(HttpStatusCode.Created)
        {
            Content = new StringContent(
                $$"""
                {"payloadUrl":"{{payloadUrl ?? $"{Origin}/api/handoffs/{Token}"}}","expiresAt":"{{(expiresAt ?? Now.AddMinutes(10)):yyyy-MM-ddTHH:mm:ssZ}}"}
                """,
                Encoding.UTF8,
                "application/json")
        };

    private static RoutePacerRelayClient Client(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        Action<HttpClient>? captureClient = null)
    {
        var httpClient = new HttpClient(new DelegateHandler(handler)) { BaseAddress = new Uri(Origin) };
        captureClient?.Invoke(httpClient);
        return new RoutePacerRelayClient(
            httpClient,
            RoutePacerHandoffOptionsTests.Enabled(),
            new FakeTimeProvider(Now));
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    private sealed class ReadRecordingContent(string body) : HttpContent
    {
        public bool WasRead { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            WasRead = true;
            return stream.WriteAsync(Encoding.UTF8.GetBytes(body)).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = Encoding.UTF8.GetByteCount(body);
            return true;
        }
    }
}

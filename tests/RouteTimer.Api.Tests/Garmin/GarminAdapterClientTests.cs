using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RouteTimer.Api.Garmin;
using RouteTimer.Services.Garmin;

namespace RouteTimer.Api.Tests.Garmin;

public sealed class GarminAdapterClientTests
{
    [Fact]
    public async Task Login_posts_camel_case_credentials_and_reads_login()
    {
        var client = CreateClient(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/auth/login", request.RequestUri!.AbsolutePath);
            Assert.Equal("{\"email\":\"rider@example.com\",\"password\":\"secret\"}", await request.Content!.ReadAsStringAsync(cancellationToken));
            return JsonResponse("{\"state\":\"connected\",\"tokenJson\":\"token-json\",\"garminUserId\":\"42\",\"displayName\":\"Jamie\"}");
        });

        var result = await client.LoginAsync("rider@example.com", "secret", CancellationToken.None);

        Assert.Equal(new GarminAdapterLogin("connected", null, "token-json", "42", "Jamie"), result);
    }

    [Fact]
    public async Task Complete_mfa_posts_camel_case_challenge()
    {
        var client = CreateClient(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/auth/mfa", request.RequestUri!.AbsolutePath);
            Assert.Equal("{\"challengeId\":\"challenge-1\",\"code\":\"123456\"}", await request.Content!.ReadAsStringAsync(cancellationToken));
            return JsonResponse("{\"state\":\"connected\",\"tokenJson\":\"token-json\",\"garminUserId\":\"42\",\"displayName\":\"Jamie\"}");
        });

        var result = await client.CompleteMfaAsync("challenge-1", "123456", CancellationToken.None);

        Assert.Equal("connected", result.State);
        Assert.Equal("token-json", result.TokenJson);
    }

    [Fact]
    public async Task Validate_posts_token_and_reads_session()
    {
        var client = CreateClient(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/auth/validate", request.RequestUri!.AbsolutePath);
            Assert.Equal("{\"token\":\"token-json\"}", await request.Content!.ReadAsStringAsync(cancellationToken));
            return JsonResponse("{\"tokenJson\":\"rotated-token\",\"garminUserId\":\"42\",\"displayName\":\"Jamie\"}");
        });

        var result = await client.ValidateAsync("token-json", CancellationToken.None);

        Assert.Equal(new GarminAdapterSession("rotated-token", "42", "Jamie"), result);
    }

    [Fact]
    public async Task Get_activities_posts_offset_and_reads_page()
    {
        var client = CreateClient(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/activities/page", request.RequestUri!.AbsolutePath);
            Assert.Equal("{\"token\":\"token-json\",\"offset\":50}", await request.Content!.ReadAsStringAsync(cancellationToken));
            return JsonResponse("""
                {"activities":[{"activityId":"123","name":"Morning ride","startedAt":"2026-08-25T08:00:00Z","activityType":"road-cycling","distanceMetres":1234.5,"durationSeconds":3600,"ascentMetres":100,"averagePowerWatts":200}],"nextOffset":100,"tokenJson":"rotated-token"}
                """);
        });

        var result = await client.GetActivitiesAsync("token-json", 50, CancellationToken.None);

        var activity = Assert.Single(result.Activities);
        Assert.Equal("123", activity.ActivityId);
        Assert.Equal("Morning ride", activity.Name);
        Assert.Equal(DateTimeOffset.Parse("2026-08-25T08:00:00Z"), activity.StartedAt);
        Assert.Equal("road-cycling", activity.ActivityType);
        Assert.Equal(100, result.NextOffset);
        Assert.Equal("rotated-token", result.TokenJson);
    }

    [Fact]
    public async Task Get_activity_escapes_id_and_returns_activity_with_rotated_token()
    {
        var client = CreateClient(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/activities/123%2Funsafe/summary", request.RequestUri!.AbsolutePath);
            Assert.Equal("{\"token\":\"token-json\"}", await request.Content!.ReadAsStringAsync(cancellationToken));
            return JsonResponse("""
                {"activity":{"activityId":"123","name":"Morning ride","startedAt":"2026-08-25T08:00:00Z","activityType":"road-cycling","distanceMetres":1234.5,"durationSeconds":3600,"ascentMetres":100,"averagePowerWatts":200},"tokenJson":"rotated-token"}
                """);
        });

        var result = await client.GetActivityAsync("token-json", "123/unsafe", CancellationToken.None);

        Assert.Equal("123", result.Activity.ActivityId);
        Assert.Equal("rotated-token", result.TokenJson);
    }

    [Fact]
    public async Task Clear_challenges_sends_delete_without_a_body()
    {
        var client = CreateClient((request, _) =>
        {
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal("/v1/auth/challenges", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Content);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });

        await client.ClearChallengesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Download_fit_streams_content_and_decodes_unpadded_token_header()
    {
        var stream = new TrackingStream("fit-bytes");
        var client = CreateClient((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/activities/123/fit", request.RequestUri!.AbsolutePath);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            };
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "123.fit" };
            response.Headers.Add("X-RouteTimer-Garmin-Token", Base64Url("{\"di_token\":\"rotated\"}"));
            return Task.FromResult(response);
        });

        var download = await client.DownloadFitAsync("token-json", "123", CancellationToken.None);

        Assert.Equal("123.fit", download.FileName);
        Assert.Equal("{\"di_token\":\"rotated\"}", download.TokenJson);
        await download.DisposeAsync();
        Assert.True(stream.WasDisposed);
    }

    [Theory]
    [InlineData("credentials-rejected", GarminAdapterError.CredentialsRejected)]
    [InlineData("mfa-invalid", GarminAdapterError.MfaInvalid)]
    [InlineData("authentication", GarminAdapterError.Authentication)]
    [InlineData("challenge-expired", GarminAdapterError.ChallengeExpired)]
    [InlineData("rate-limited", GarminAdapterError.RateLimited)]
    [InlineData("unavailable", GarminAdapterError.Unavailable)]
    [InlineData("response-invalid", GarminAdapterError.ResponseInvalid)]
    [InlineData("request-invalid", GarminAdapterError.RequestInvalid)]
    [InlineData("activity-not-allowed", GarminAdapterError.ActivityNotAllowed)]
    [InlineData("fit-too-large", GarminAdapterError.FitTooLarge)]
    public async Task Adapter_error_codes_map_without_retaining_response_details(string code, GarminAdapterError expected)
    {
        var client = CreateClient((_, _) => Task.FromResult(JsonResponse($"{{\"code\":\"{code}\",\"detail\":\"secret detail\",\"unknown\":\"secret field\"}}", HttpStatusCode.BadRequest)));

        var exception = await Assert.ThrowsAsync<GarminAdapterException>(() => client.ValidateAsync("token-json", CancellationToken.None));

        Assert.Equal(expected, exception.Error);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_error_code_and_malformed_success_json_map_to_response_invalid()
    {
        var unknownCodeClient = CreateClient((_, _) => Task.FromResult(JsonResponse("{\"code\":\"unknown\",\"detail\":\"secret\"}", HttpStatusCode.BadRequest)));
        var malformedJsonClient = CreateClient((_, _) => Task.FromResult(JsonResponse("not-json")));

        var unknownCode = await Assert.ThrowsAsync<GarminAdapterException>(() => unknownCodeClient.ValidateAsync("token-json", CancellationToken.None));
        var malformedJson = await Assert.ThrowsAsync<GarminAdapterException>(() => malformedJsonClient.ValidateAsync("token-json", CancellationToken.None));

        Assert.Equal(GarminAdapterError.ResponseInvalid, unknownCode.Error);
        Assert.Equal(GarminAdapterError.ResponseInvalid, malformedJson.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-base64url")]
    [InlineData("bm90LWpzb24")]
    public async Task Download_fit_rejects_missing_or_invalid_token_header(string? header)
    {
        var client = CreateClient((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
            if (header is not null)
            {
                response.Headers.Add("X-RouteTimer-Garmin-Token", header);
            }

            return Task.FromResult(response);
        });

        var exception = await Assert.ThrowsAsync<GarminAdapterException>(() => client.DownloadFitAsync("token-json", "123", CancellationToken.None));

        Assert.Equal(GarminAdapterError.ResponseInvalid, exception.Error);
    }

    [Fact]
    public async Task Download_fit_rejects_duplicate_token_headers()
    {
        var client = CreateClient((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
            response.Headers.Add("X-RouteTimer-Garmin-Token", [Base64Url("{\"token\":\"one\"}"), Base64Url("{\"token\":\"two\"}")]);
            return Task.FromResult(response);
        });

        var exception = await Assert.ThrowsAsync<GarminAdapterException>(() => client.DownloadFitAsync("token-json", "123", CancellationToken.None));

        Assert.Equal(GarminAdapterError.ResponseInvalid, exception.Error);
    }

    [Fact]
    public async Task Cancellation_is_propagated_without_adapter_error_mapping()
    {
        using var cancellation = new CancellationTokenSource();
        var client = CreateClient((_, token) => Task.FromCanceled<HttpResponseMessage>(token));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ValidateAsync("token-json", cancellation.Token));
    }

    [Fact]
    public async Task Transport_failure_maps_to_distinct_adapter_unavailable_error()
    {
        var client = CreateClient((_, _) => throw new HttpRequestException("internal adapter endpoint"));

        var exception = await Assert.ThrowsAsync<GarminAdapterException>(() => client.ValidateAsync("token-json", CancellationToken.None));

        Assert.Equal(GarminAdapterError.AdapterUnavailable, exception.Error);
        Assert.DoesNotContain("internal adapter endpoint", exception.Message, StringComparison.Ordinal);
    }

    private static GarminAdapterClient CreateClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(new HttpClient(new DelegateHandler(handler)) { BaseAddress = new Uri("http://garmin-adapter.invalid/") });

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    private sealed class TrackingStream(string contents) : MemoryStream(Encoding.UTF8.GetBytes(contents))
    {
        public bool WasDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            WasDisposed = true;
            return base.DisposeAsync();
        }
    }
}

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using RouteTimer.Client.Api;
using RouteTimer.Contracts.Auth;
using RouteTimer.Contracts.Jobs;
using RouteTimer.Contracts.Models;
using RouteTimer.Contracts.Predictions;
using RouteTimer.Contracts.Profile;
using RouteTimer.Contracts.Training;

namespace RouteTimer.Client.Tests.Api;

public sealed class RouteTimerApiClientTests
{
    [Fact]
    public async Task GetProfileAsync_requests_profile_and_maps_not_found_to_null()
    {
        HttpRequestMessage? captured = null;
        var client = CreateApiClient((request, _) =>
        {
            captured = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var profile = await client.GetProfileAsync(CancellationToken.None);

        Assert.Null(profile);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured!.Method);
        Assert.Equal("/api/profile", captured.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task UpdateProfileAsync_puts_json_to_profile_endpoint()
    {
        var request = new UpdateProfileRequest(72.4, 8.7);
        var response = new ProfileResponse(72.4, 8.7);
        var client = CreateApiClient(async (httpRequest, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Put, httpRequest.Method);
            Assert.Equal("/api/profile", httpRequest.RequestUri!.AbsolutePath);

            var payload = await httpRequest.Content!.ReadFromJsonAsync<UpdateProfileRequest>(cancellationToken);
            Assert.Equal(request, payload);

            return JsonResponse(response);
        });

        var result = await client.UpdateProfileAsync(request, CancellationToken.None);

        Assert.Equal(response, result);
    }

    [Fact]
    public async Task GetTrainingActivitiesAsync_gets_collection_and_deserializes_response()
    {
        var expected = new[]
        {
            TrainingSummary("morning.fit"),
            TrainingSummary("evening.fit")
        };
        var client = CreateApiClient((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/training-activities", request.RequestUri!.AbsolutePath);
            return Task.FromResult(JsonResponse<IReadOnlyList<TrainingActivitySummaryResponse>>(expected));
        });

        var result = await client.GetTrainingActivitiesAsync(CancellationToken.None);

        AssertJsonEquivalent(expected, result);
    }

    [Fact]
    public async Task GetTrainingActivityAsync_gets_detail_and_maps_not_found_to_null()
    {
        var activityId = Guid.NewGuid();
        var expected = new TrainingActivityDetailResponse(
            TrainingSummary("session.fit", activityId),
            new Dictionary<string, int>(StringComparer.Ordinal) { ["duplicate-samples"] = 2 });
        var calls = 0;
        var client = CreateApiClient((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"/api/training-activities/{activityId}", request.RequestUri!.AbsolutePath);

            return Task.FromResult(calls == 1
                ? JsonResponse(expected)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var found = await client.GetTrainingActivityAsync(activityId, CancellationToken.None);
        var missing = await client.GetTrainingActivityAsync(activityId, CancellationToken.None);

        AssertJsonEquivalent(expected, found);
        Assert.Null(missing);
    }

    [Fact]
    public async Task UploadTrainingActivitiesAsync_posts_multipart_with_original_filenames_and_disposes_streams()
    {
        var firstStream = new TrackingStream("fit-one");
        var secondStream = new TrackingStream("fit-two");
        var files = new[]
        {
            new ClientFileUpload("morning.fit", firstStream.Length, () => firstStream),
            new ClientFileUpload("hill-repeats.fit", secondStream.Length, () => secondStream)
        };
        var expected = new TrainingUploadBatchResponse(
            [
                new TrainingUploadFileResponse("morning.fit", "accepted", Guid.NewGuid(), Guid.NewGuid(), null),
                new TrainingUploadFileResponse("hill-repeats.fit", "invalid", null, null, "invalid-fit-upload")
            ]);

        var client = CreateApiClient(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/training-activities", request.RequestUri!.AbsolutePath);
            Assert.Equal("multipart/form-data", request.Content!.Headers.ContentType!.MediaType);

            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            Assert.Contains("filename=morning.fit", body, StringComparison.Ordinal);
            Assert.Contains("filename=hill-repeats.fit", body, StringComparison.Ordinal);

            return JsonResponse(expected, HttpStatusCode.Accepted);
        });

        var result = await client.UploadTrainingActivitiesAsync(files, CancellationToken.None);

        AssertJsonEquivalent(expected, result);
        Assert.True(firstStream.WasDisposed);
        Assert.True(secondStream.WasDisposed);
    }

    [Fact]
    public async Task DeleteTrainingActivityAsync_uses_delete_and_maps_not_found_to_false()
    {
        var id = Guid.NewGuid();
        var calls = 0;
        var client = CreateApiClient((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal($"/api/training-activities/{id}", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(calls == 1 ? HttpStatusCode.NoContent : HttpStatusCode.NotFound));
        });

        var deleted = await client.DeleteTrainingActivityAsync(id, CancellationToken.None);
        var missing = await client.DeleteTrainingActivityAsync(id, CancellationToken.None);

        Assert.True(deleted);
        Assert.False(missing);
    }

    [Fact]
    public async Task GetModelStatusAsync_gets_current_model_status()
    {
        var expected = ModelStatus();
        var client = CreateApiClient((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/models/current", request.RequestUri!.AbsolutePath);
            return Task.FromResult(JsonResponse(expected));
        });

        var result = await client.GetModelStatusAsync(CancellationToken.None);

        AssertJsonEquivalent(expected, result);
    }

    [Fact]
    public async Task RebuildModelAsync_posts_rebuild_endpoint()
    {
        var expected = new ModelRebuildResponse(Guid.NewGuid());
        var client = CreateApiClient((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/models/rebuild", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Content);
            return Task.FromResult(JsonResponse(expected, HttpStatusCode.Accepted));
        });

        var result = await client.RebuildModelAsync(CancellationToken.None);

        AssertJsonEquivalent(expected, result);
    }

    [Fact]
    public async Task GetPredictionsAsync_gets_prediction_summaries()
    {
        var expected = new[] { PredictionSummary(), PredictionSummary() };
        var client = CreateApiClient((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/predictions", request.RequestUri!.AbsolutePath);
            return Task.FromResult(JsonResponse<IReadOnlyList<PredictionSummaryResponse>>(expected));
        });

        var result = await client.GetPredictionsAsync(CancellationToken.None);

        AssertJsonEquivalent(expected, result);
    }

    [Fact]
    public async Task SubmitPredictionAsync_posts_gpx_file_with_original_filename()
    {
        var stream = new TrackingStream("gpx-body");
        var upload = new ClientFileUpload("alpine-loop.gpx", stream.Length, () => stream);
        var expected = new PredictionSubmissionResponse(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var client = CreateApiClient(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/predictions", request.RequestUri!.AbsolutePath);
            Assert.Equal("multipart/form-data", request.Content!.Headers.ContentType!.MediaType);

            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            Assert.Contains("filename=alpine-loop.gpx", body, StringComparison.Ordinal);

            return JsonResponse(expected, HttpStatusCode.Accepted);
        });

        var result = await client.SubmitPredictionAsync(upload, CancellationToken.None);

        AssertJsonEquivalent(expected, result);
        Assert.True(stream.WasDisposed);
    }

    [Fact]
    public async Task GetPredictionAsync_gets_detail_and_maps_not_found_to_null()
    {
        var predictionId = Guid.NewGuid();
        var expected = new PredictionDetailResponse(PredictionSummary(predictionId), [PredictionSegment()]);
        var calls = 0;
        var client = CreateApiClient((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"/api/predictions/{predictionId}", request.RequestUri!.AbsolutePath);

            return Task.FromResult(calls == 1
                ? JsonResponse(expected)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var found = await client.GetPredictionAsync(predictionId, CancellationToken.None);
        var missing = await client.GetPredictionAsync(predictionId, CancellationToken.None);

        AssertJsonEquivalent(expected, found);
        Assert.Null(missing);
    }

    [Fact]
    public async Task DeletePredictionAsync_uses_delete_and_maps_not_found_to_false()
    {
        var id = Guid.NewGuid();
        var calls = 0;
        var client = CreateApiClient((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal($"/api/predictions/{id}", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(calls == 1 ? HttpStatusCode.NoContent : HttpStatusCode.NotFound));
        });

        var deleted = await client.DeletePredictionAsync(id, CancellationToken.None);
        var missing = await client.DeletePredictionAsync(id, CancellationToken.None);

        Assert.True(deleted);
        Assert.False(missing);
    }

    [Fact]
    public async Task GetJobAsync_gets_job_and_maps_not_found_to_null()
    {
        var jobId = Guid.NewGuid();
        var expected = Job(jobId, "Running", 25, "processing-route");
        var calls = 0;
        var client = CreateApiClient((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"/api/jobs/{jobId}", request.RequestUri!.AbsolutePath);

            return Task.FromResult(calls == 1
                ? JsonResponse(expected)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        });

        var found = await client.GetJobAsync(jobId, CancellationToken.None);
        var missing = await client.GetJobAsync(jobId, CancellationToken.None);

        Assert.Equal(expected, found);
        Assert.Null(missing);
    }

    [Fact]
    public async Task Client_throws_problem_exception_with_safe_fields_and_validation_errors()
    {
        var longValue = new string('x', 640);
        var client = CreateApiClient((request, _) =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);

            return Task.FromResult(ProblemResponse(
                HttpStatusCode.BadRequest,
                """
                {
                  "title": "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
                  "detail": "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
                  "code": "invalid-profile",
                  "errors": {
                    "riderWeightKg": [
                      "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
                    ]
                  }
                }
                """));
        });

        var exception = await Assert.ThrowsAsync<ApiProblemException>(() =>
            client.UpdateProfileAsync(new UpdateProfileRequest(20, 1), CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal("invalid-profile", exception.Code);
        Assert.Equal(512, exception.Title.Length);
        Assert.Equal(512, exception.Detail!.Length);
        Assert.Equal(512, exception.Errors["riderWeightKg"][0].Length);
    }

    [Fact]
    public async Task Client_throws_safe_fallback_problem_exception_when_problem_body_is_missing_or_malformed()
    {
        const string rawBody = "<html>Database exploded with stacktrace</html>";
        var client = CreateApiClient((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent(rawBody, Encoding.UTF8, "text/html")
            };

            return Task.FromResult(response);
        });

        var exception = await Assert.ThrowsAsync<ApiProblemException>(() =>
            client.GetPredictionsAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Equal("request-failed", exception.Code);
        Assert.DoesNotContain("Database exploded", exception.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(exception.Errors);
    }

    [Fact]
    public async Task Client_forwards_the_caller_cancellation_token_to_http_requests()
    {
        CancellationToken observed = CancellationToken.None;
        var client = CreateApiClient((_, cancellationToken) =>
        {
            observed = cancellationToken;
            return Task.FromCanceled<HttpResponseMessage>(new CancellationToken(canceled: true));
        });
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetPredictionsAsync(cts.Token));
        Assert.True(observed.CanBeCanceled);
    }

    // The failure mode this test class exists to prevent: SetupLocalCredentialAsync and
    // LocalLoginAsync are one character apart in URL and wildly different in effect -- swapping
    // them would silently *set* a fresh install's passphrase to whatever a login attempt typed.
    [Fact]
    public async Task SetupLocalCredentialAsync_posts_the_passphrase_to_the_setup_endpoint()
    {
        HttpRequestMessage? captured = null;
        SetLocalCredentialRequest? payload = null;
        var client = CreateApiClient(async (request, ct) =>
        {
            captured = request;
            payload = await request.Content!.ReadFromJsonAsync<SetLocalCredentialRequest>(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var result = await client.SetupLocalCredentialAsync("correct horse battery staple", CancellationToken.None);

        Assert.True(result);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("/api/auth/setup", captured.RequestUri!.AbsolutePath);
        Assert.Equal("correct horse battery staple", payload?.Passphrase);
    }

    [Fact]
    public async Task LocalLoginAsync_posts_the_passphrase_to_the_login_endpoint()
    {
        HttpRequestMessage? captured = null;
        LocalLoginRequest? payload = null;
        var client = CreateApiClient(async (request, ct) =>
        {
            captured = request;
            payload = await request.Content!.ReadFromJsonAsync<LocalLoginRequest>(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var result = await client.LocalLoginAsync("correct horse battery staple", CancellationToken.None);

        Assert.True(result);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("/api/auth/login", captured.RequestUri!.AbsolutePath);
        Assert.Equal("correct horse battery staple", payload?.Passphrase);
    }

    private static RouteTimerApiClient CreateApiClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
        new(new HttpClient(new DelegateHandler(handler)) { BaseAddress = new Uri("https://example.test", UriKind.Absolute) });

    private static HttpResponseMessage JsonResponse<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(value) };

    private static HttpResponseMessage ProblemResponse(HttpStatusCode status, string json) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/problem+json")
        };

    private static void AssertJsonEquivalent<T>(T expected, T actual) =>
        Assert.Equal(
            JsonSerializer.Serialize(expected, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            JsonSerializer.Serialize(actual, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    private static TrainingActivitySummaryResponse TrainingSummary(string fileName, Guid? id = null) => new(
        id ?? Guid.NewGuid(),
        Guid.NewGuid(),
        fileName,
        DateTimeOffset.Parse("2026-08-25T06:00:00Z", CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-08-25T07:30:00Z", CultureInfo.InvariantCulture),
        "Garmin",
        "Edge 1040",
        42123.4,
        812.5,
        5021,
        "Eligible",
        1,
        1,
        0.98,
        0.85,
        ["power-gap"],
        DateTimeOffset.Parse("2026-08-25T08:00:00Z", CultureInfo.InvariantCulture));

    private static ModelStatusResponse ModelStatus() => new(
        true,
        null,
        Guid.NewGuid(),
        "v1.0.0",
        DateTimeOffset.Parse("2026-08-20T12:00:00Z", CultureInfo.InvariantCulture),
        true,
        true,
        "Validated",
        0.082,
        0.156,
        new PhysicalCoefficientsResponse(0.97, 1.225, 0.0045, 0.31),
        [new PowerBandCoverageResponse("flat", "5m", 255, 2400, 8, 0.15, "High")],
        16,
        2,
        Job(Guid.NewGuid(), "Running", 70, "building-power-model"));

    private static PredictionSummaryResponse PredictionSummary(Guid? id = null) => new(
        id ?? Guid.NewGuid(),
        "Succeeded",
        28750,
        420,
        3610,
        7.96,
        245.2,
        "High",
        ["tailwind-estimated"],
        Guid.NewGuid(),
        "v1.0.0",
        true,
        "Validated",
        0.082,
        0.156,
        71.3,
        8.4,
        "dry-road",
        "calm",
        "temperate",
        true,
        DateTimeOffset.Parse("2026-08-25T09:30:00Z", CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-08-25T09:45:00Z", CultureInfo.InvariantCulture));

    private static PredictionSegmentResponse PredictionSegment() => new(
        1,
        51.5074,
        -0.1278,
        20,
        1200,
        1200,
        0.021,
        0.0003,
        252,
        8.2,
        130,
        130,
        "Medium");

    private static JobResponse Job(Guid id, string state, int progressPercent, string progressStage) => new(
        id,
        "PredictRoute",
        Guid.NewGuid(),
        state,
        progressPercent,
        progressStage,
        2,
        DateTimeOffset.Parse("2026-08-25T08:00:00Z", CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-08-25T08:01:00Z", CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-08-25T08:02:00Z", CultureInfo.InvariantCulture),
        state.Equals("Succeeded", StringComparison.OrdinalIgnoreCase)
            || state.Equals("Failed", StringComparison.OrdinalIgnoreCase)
            || state.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
            ? DateTimeOffset.Parse("2026-08-25T08:03:00Z", CultureInfo.InvariantCulture)
            : null,
        state.Equals("Running", StringComparison.OrdinalIgnoreCase)
            ? DateTimeOffset.Parse("2026-08-25T08:04:00Z", CultureInfo.InvariantCulture)
            : null,
        state.Equals("Failed", StringComparison.OrdinalIgnoreCase) ? "processing-failed" : null,
        state.Equals("Failed", StringComparison.OrdinalIgnoreCase) ? "Safe problem detail" : null);

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

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RouteTimer.Contracts.Adjustments;
using RouteTimer.Contracts.Predictions;
using RouteTimer.Domain.Adjustments;
using RouteTimer.Domain.Models;
using RouteTimer.Domain.Physics;
using RouteTimer.Domain.Profile;
using RouteTimer.Persistence;
using RouteTimer.Persistence.Repositories;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Api.Tests.Endpoints;

public sealed partial class PredictionAdjustmentEndpointTests
{
    // Break caught: the newly exposed adjustment resources accidentally bypass the fallback rider policy.
    [Theory]
    [InlineData("/api/pacing-strategies")]
    [InlineData("/api/predictions/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/adjustments")]
    public async Task Adjustment_resources_require_authentication(string path)
    {
        await using var app = new RouteTimerApiFactory();
        using var client = app.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Capabilities_reflect_configured_flags()
    {
        await using var app = CreateRiderApp()
            .WithSetting("PacingStrategies:Enabled", "true")
            .WithSetting("PacingStrategies:TimeTarget", "true")
            .WithSetting("PacingStrategies:NpIfTarget", "false");
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<PacingStrategyCapabilityResponse>("/api/pacing-strategies");

        Assert.NotNull(response);
        Assert.True(response.Enabled);
        Assert.True(response.TimeTarget);
        Assert.False(response.NpIfTarget);
        Assert.Equal(65536, response.MaximumDefinitionBytes);
    }

    // Break caught: the default deployment configuration leaves pacing adjustments or one of its
    // delivered strategies unavailable, so the UI continues to hide part of the feature.
    [Fact]
    public async Task Capabilities_enable_every_delivered_pacing_strategy_by_default()
    {
        await using var app = CreateRiderApp();
        using var client = app.CreateClient();

        var response = await client.GetFromJsonAsync<PacingStrategyCapabilityResponse>("/api/pacing-strategies");

        Assert.NotNull(response);
        Assert.True(response.Enabled);
        Assert.True(response.SegmentSpecificGains);
        Assert.True(response.NpIfTarget);
        Assert.True(response.TimeTarget);
        Assert.True(response.RpeZoneShift);
        Assert.True(response.VariableMatchBurning);
    }

    // Break caught: explicitly disabling the parent flag is not actually enforced by the endpoint.
    [Fact]
    public async Task Create_rejects_every_strategy_when_the_parent_flag_is_disabled()
    {
        await using var app = CreateRiderApp()
            .WithSetting("PacingStrategies:Enabled", "false");
        var predictionId = await SeedSucceededBaselineAsync(app.Services);
        using var client = app.CreateClient();

        using var response = await client.PostAsJsonAsync<PacingStrategyRequest>(
            $"/api/predictions/{predictionId}/adjustments",
            new TimeTargetRequest(1200, "proportional", null, false));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("pacing-strategy-disabled", await response.Content.ReadAsStringAsync());
    }

    // Break caught: enabling the parent flag alone (without the per-strategy flag) is enough to accept a request.
    [Fact]
    public async Task Create_rejects_a_strategy_disabled_individually_even_when_the_parent_is_enabled()
    {
        await using var app = CreateRiderApp()
            .WithSetting("PacingStrategies:Enabled", "true")
            .WithSetting("PacingStrategies:TimeTarget", "false");
        var predictionId = await SeedSucceededBaselineAsync(app.Services);
        using var client = app.CreateClient();

        using var response = await client.PostAsJsonAsync<PacingStrategyRequest>(
            $"/api/predictions/{predictionId}/adjustments",
            new TimeTargetRequest(1200, "proportional", null, false));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("pacing-strategy-disabled", await response.Content.ReadAsStringAsync());
    }

    // Break caught: malformed JSON or an unrecognized strategy discriminator leaks an unhandled 500 or the framework's own untyped 400.
    [Theory]
    [InlineData("not json")]
    [InlineData("{\"type\":\"not-a-real-strategy\"}")]
    [InlineData("{}")]
    public async Task Create_returns_a_stable_bad_request_for_malformed_or_unknown_strategy_json(string body)
    {
        await using var app = CreateRiderApp();
        var predictionId = await SeedSucceededBaselineAsync(app.Services);
        using var client = app.CreateClient();

        using var response = await client.PostAsync(
            $"/api/predictions/{predictionId}/adjustments",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("pacing-strategy-invalid", await response.Content.ReadAsStringAsync());
    }

    // Break caught: listing adjustments under an unrelated or nonexistent baseline id returns someone else's children.
    [Fact]
    public async Task List_is_newest_first_and_scoped_to_its_own_baseline()
    {
        await using var app = CreateRiderApp();
        var predictionId = await SeedSucceededBaselineAsync(app.Services);
        var otherPredictionId = await SeedSucceededBaselineAsync(app.Services);
        var first = await SeedAdjustmentAsync(app.Services, predictionId);
        var second = await SeedAdjustmentAsync(app.Services, predictionId);
        await SeedAdjustmentAsync(app.Services, otherPredictionId);
        using var client = app.CreateClient();

        var summaries = await client.GetFromJsonAsync<List<PredictionAdjustmentSummaryResponse>>($"/api/predictions/{predictionId}/adjustments");

        Assert.NotNull(summaries);
        Assert.Equal([second, first], summaries.Select(summary => summary.Id));
    }

    // Break caught: fetching or deleting a real adjustment id through the wrong baseline in the URL leaks its existence.
    [Fact]
    public async Task Detail_and_delete_return_404_when_the_adjustment_belongs_to_a_different_baseline()
    {
        await using var app = CreateRiderApp();
        var predictionId = await SeedSucceededBaselineAsync(app.Services);
        var otherPredictionId = await SeedSucceededBaselineAsync(app.Services);
        var adjustmentId = await SeedAdjustmentAsync(app.Services, predictionId);
        using var client = app.CreateClient();

        using var detail = await client.GetAsync($"/api/predictions/{otherPredictionId}/adjustments/{adjustmentId}");
        using var delete = await client.DeleteAsync($"/api/predictions/{otherPredictionId}/adjustments/{adjustmentId}");

        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Contains("adjustment-not-found", await detail.Content.ReadAsStringAsync());

        using var stillThere = await client.GetAsync($"/api/predictions/{predictionId}/adjustments/{adjustmentId}");
        Assert.Equal(HttpStatusCode.OK, stillThere.StatusCode);
    }

    [Fact]
    public async Task Detail_exposes_the_canonical_strategy_json_and_delete_removes_it()
    {
        await using var app = CreateRiderApp();
        var predictionId = await SeedSucceededBaselineAsync(app.Services);
        var adjustmentId = await SeedAdjustmentAsync(app.Services, predictionId, """{"type":"timeTarget","targetMovingSeconds":1200}""");
        using var client = app.CreateClient();

        var detail = await client.GetFromJsonAsync<PredictionAdjustmentDetailResponse>($"/api/predictions/{predictionId}/adjustments/{adjustmentId}");
        Assert.NotNull(detail);
        Assert.Equal("timeTarget", detail.Strategy.GetProperty("type").GetString());
        Assert.Equal(1200, detail.Strategy.GetProperty("targetMovingSeconds").GetInt32());
        Assert.Equal(AdjustmentState.Queued.ToString(), detail.Summary.State);

        using var delete = await client.DeleteAsync($"/api/predictions/{predictionId}/adjustments/{adjustmentId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var afterDelete = await client.GetAsync($"/api/predictions/{predictionId}/adjustments/{adjustmentId}");
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
    }

    [Fact]
    public async Task Delete_of_an_unknown_adjustment_returns_404()
    {
        await using var app = CreateRiderApp();
        var predictionId = await SeedSucceededBaselineAsync(app.Services);
        using var client = app.CreateClient();

        using var response = await client.DeleteAsync($"/api/predictions/{predictionId}/adjustments/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // Break caught: the first real strategy's own vertical slice never reaches a genuine 202, only NotImplementedException.
    [Fact]
    public async Task Create_accepts_a_SegmentSpecificGains_request_once_its_flags_are_enabled()
    {
        await using var app = CreateRiderApp()
            .WithSetting("PacingStrategies:Enabled", "true")
            .WithSetting("PacingStrategies:SegmentSpecificGains", "true");
        var predictionId = await SeedSucceededBaselineAsync(app.Services);
        using var client = app.CreateClient();

        using var response = await client.PostAsJsonAsync<PacingStrategyRequest>(
            $"/api/predictions/{predictionId}/adjustments",
            new SegmentSpecificGainsRequest([new SegmentGainsRuleRequest(.02, null, null, null, null, null, 1.1, null)]));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var submission = await response.Content.ReadFromJsonAsync<PredictionAdjustmentSubmissionResponse>();
        Assert.NotNull(submission);

        var detail = await client.GetFromJsonAsync<PredictionAdjustmentDetailResponse>($"/api/predictions/{predictionId}/adjustments/{submission.AdjustmentId}");
        Assert.NotNull(detail);
        Assert.Equal("segmentSpecificGains", detail.Strategy.GetProperty("type").GetString());
        Assert.Equal(AdjustmentState.Queued.ToString(), detail.Summary.State);
    }

    // Break caught: an over-specified rule (more than one selector, or neither/both of factor and delta) is accepted and silently misbehaves later.
    [Fact]
    public async Task Create_returns_SegmentSpecificGains_field_errors_next_to_the_offending_rule()
    {
        await using var app = CreateRiderApp()
            .WithSetting("PacingStrategies:Enabled", "true")
            .WithSetting("PacingStrategies:SegmentSpecificGains", "true");
        var predictionId = await SeedSucceededBaselineAsync(app.Services);
        using var client = app.CreateClient();

        using var response = await client.PostAsJsonAsync<PacingStrategyRequest>(
            $"/api/predictions/{predictionId}/adjustments",
            new SegmentSpecificGainsRequest([
                new SegmentGainsRuleRequest(.02, null, null, null, null, null, 1.1, null),
                new SegmentGainsRuleRequest(.02, null, 1, null, null, null, 1.1, null),
                new SegmentGainsRuleRequest(null, .05, null, null, null, null, 1.1, 5),
            ]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("pacing-strategy-invalid", body);
        Assert.Contains("rules[1]", body);
        Assert.Contains("rules[2]", body);
        Assert.DoesNotContain("\"rules[0]\"", body);
    }

    // Break caught: the ten-rule limit is only documented, never enforced, once a strategy is actually deliverable.
    [Fact]
    public async Task Create_rejects_more_than_ten_SegmentSpecificGains_rules()
    {
        await using var app = CreateRiderApp()
            .WithSetting("PacingStrategies:Enabled", "true")
            .WithSetting("PacingStrategies:SegmentSpecificGains", "true");
        var predictionId = await SeedSucceededBaselineAsync(app.Services);
        using var client = app.CreateClient();
        var rules = Enumerable.Range(0, 11)
            .Select(index => new SegmentGainsRuleRequest(null, null, index, index, null, null, 1.1, null))
            .ToList();

        using var response = await client.PostAsJsonAsync<PacingStrategyRequest>(
            $"/api/predictions/{predictionId}/adjustments",
            new SegmentSpecificGainsRequest(rules));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"rules\"", await response.Content.ReadAsStringAsync());
    }

    private static RouteTimerApiFactory CreateRiderApp(Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? configure = null)
        => new RouteTimerApiFactory().WithRiderAuthentication(configure);

    private static async Task<Guid> SeedSucceededBaselineAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteTimerDbContext>();
        var models = new RiderModelRepository(context);
        var profile = new RiderProfile(75, 10);
        var modelId = await models.SaveAsync(
            new RiderModel(new PowerModel([], 200), PhysicalCoefficients.Default, DescentLimitModel.Conservative, false, "v1"),
            profile, new ModelValidationSummary(ModelValidationStatus.Passed, .05, .08), CancellationToken.None);
        var model = (await models.GetAsync(modelId, CancellationToken.None))!;

        var predictions = new PredictionRepository(context);
        var created = await predictions.CreateQueuedAsync(new QueuedPredictionCreation(
            new StoredUpload(Guid.NewGuid(), "route.gpx", "gpx", [1, 2, 3], Enumerable.Repeat((byte)9, 32).ToArray(), DateTimeOffset.UtcNow),
            model, profile, PredictionAssumptions.RoadCalmDryMovingOnly, DateTimeOffset.UtcNow), CancellationToken.None);

        var job = await context.Jobs.SingleAsync(entity => entity.Id == created.JobId);
        job.State = "Running";
        job.WorkerId = "endpoint-test-worker";
        await context.SaveChangesAsync();

        await predictions.TryPublishAsync(created.PredictionId, created.JobId, "endpoint-test-worker",
            new PredictionPublication(100, 5, TimeSpan.FromSeconds(20), 5, 200, ConfidenceLevel.Medium, [],
                [new PersistedPredictionSegment(1, 51.1, -2.1, 100, 100, 100, .02, 0, 200, 5, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20), ConfidenceLevel.Medium)]),
            CancellationToken.None);
        return created.PredictionId;
    }

    private static async Task<Guid> SeedAdjustmentAsync(IServiceProvider services, Guid predictionId, string? strategyJson = null)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteTimerDbContext>();
        var repository = new PredictionAdjustmentRepository(context);
        var result = await repository.CreateQueuedAsync(
            new QueuedAdjustmentCreation(predictionId, PacingStrategyType.TimeTarget, strategyJson ?? """{"type":"timeTarget"}""", DateTimeOffset.UtcNow),
            CancellationToken.None);
        return result.AdjustmentId!.Value;
    }
}

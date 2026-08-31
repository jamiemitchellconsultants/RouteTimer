using System.Net;
using System.Net.Http.Json;
using System.Text;
using RouteTimer.Contracts.Adjustments;
using RouteTimer.Contracts.Errors;
using Xunit;

namespace RouteTimer.Api.Tests.Endpoints;

public partial class PredictionAdjustmentEndpointTests
{
    // Break caught: JSON that omits a strategy's collection entirely leaves the mapper dereferencing
    // null and returning a 500 instead of a field-keyed validation problem.
    [Theory]
    [InlineData("segment-specific-gains", "SegmentSpecificGains", "rules")]
    [InlineData("rpe-zone-shift", "RpeZoneShift", "assignments")]
    [InlineData("variable-match-burning", "VariableMatchBurning", "windows")]
    public async Task Create_rejects_a_missing_collection_as_a_field_error(string discriminator, string flag, string field)
    {
        await using var app = CreateRiderApp()
            .WithSetting("PacingStrategies:Enabled", "true")
            .WithSetting($"PacingStrategies:{flag}", "true");
        var predictionId = await SeedSucceededBaselineAsync(app.Services);
        using var client = app.CreateClient();

        using var response = await client.PostAsync(
            $"/api/predictions/{predictionId}/adjustments",
            new StringContent($$"""{"type":"{{discriminator}}","thresholdMode":"model-inferred"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains($"\"{field}\"", body, StringComparison.Ordinal);
        Assert.Contains(ErrorCodes.PacingStrategyInvalid, body, StringComparison.Ordinal);
    }

    // Break caught: an explicit null collection takes a different code path from an absent one.
    [Fact]
    public async Task Create_rejects_an_explicitly_null_collection_as_a_field_error()
    {
        await using var app = CreateRiderApp()
            .WithSetting("PacingStrategies:Enabled", "true")
            .WithSetting("PacingStrategies:SegmentSpecificGains", "true");
        var predictionId = await SeedSucceededBaselineAsync(app.Services);
        using var client = app.CreateClient();

        using var response = await client.PostAsync(
            $"/api/predictions/{predictionId}/adjustments",
            new StringContent("""{"type":"segment-specific-gains","rules":null}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"rules\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // Break caught: a null entry inside an otherwise well-formed collection dereferences null.
    [Fact]
    public async Task Create_rejects_a_null_collection_entry_as_a_field_error()
    {
        await using var app = CreateRiderApp()
            .WithSetting("PacingStrategies:Enabled", "true")
            .WithSetting("PacingStrategies:SegmentSpecificGains", "true");
        var predictionId = await SeedSucceededBaselineAsync(app.Services);
        using var client = app.CreateClient();

        using var response = await client.PostAsync(
            $"/api/predictions/{predictionId}/adjustments",
            new StringContent("""{"type":"segment-specific-gains","rules":[null]}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"rules[0]\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_rejects_more_than_ten_zone_shift_assignments()
    {
        await using var app = CreateRiderApp()
            .WithSetting("PacingStrategies:Enabled", "true")
            .WithSetting("PacingStrategies:RpeZoneShift", "true");
        var predictionId = await SeedSucceededBaselineAsync(app.Services);
        using var client = app.CreateClient();
        var assignments = Enumerable.Range(0, 11)
            .Select(index => new ZoneAssignmentRequest(false, index * 0.01, (index * 0.01) + 0.005, 3, "midpoint"))
            .ToList();

        using var response = await client.PostAsJsonAsync<PacingStrategyRequest>(
            $"/api/predictions/{predictionId}/adjustments",
            new RpeZoneShiftRequest("model-inferred", null, assignments));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"assignments\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_rejects_more_than_ten_match_burning_windows()
    {
        await using var app = CreateRiderApp()
            .WithSetting("PacingStrategies:Enabled", "true")
            .WithSetting("PacingStrategies:VariableMatchBurning", "true");
        var predictionId = await SeedSucceededBaselineAsync(app.Services);
        using var client = app.CreateClient();
        var windows = Enumerable.Range(0, 11)
            .Select(index => new MatchBurnWindowRequest(
                "sequence", null, null, null, null, index + 1, index + 1, "percent-cp", null, 1.2, null))
            .ToList();

        using var response = await client.PostAsJsonAsync<PacingStrategyRequest>(
            $"/api/predictions/{predictionId}/adjustments",
            new VariableMatchBurningRequest(250, 15000, windows, 120, 0.8, 300, 0.7, true, false));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"windows\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // Break caught: a documented numeric bound is inclusive on one side in the domain and exclusive at
    // the edge the API actually accepts.
    [Theory]
    [InlineData(1.5, 250, HttpStatusCode.Accepted)]
    [InlineData(1.500001, 250, HttpStatusCode.BadRequest)]
    [InlineData(0, 250, HttpStatusCode.BadRequest)]
    [InlineData(0.85, 2000, HttpStatusCode.Accepted)]
    [InlineData(0.85, 2000.1, HttpStatusCode.BadRequest)]
    [InlineData(0.85, 0.9, HttpStatusCode.BadRequest)]
    public async Task Create_enforces_the_np_if_numeric_bounds(double targetIf, double ftpWatts, HttpStatusCode expected)
    {
        await using var app = CreateRiderApp()
            .WithSetting("PacingStrategies:Enabled", "true")
            .WithSetting("PacingStrategies:NpIfTarget", "true");
        var predictionId = await SeedSucceededBaselineAsync(app.Services);
        using var client = app.CreateClient();

        using var response = await client.PostAsJsonAsync<PacingStrategyRequest>(
            $"/api/predictions/{predictionId}/adjustments",
            new NpIfTargetRequest(targetIf, ftpWatts, "proportional"));

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData(1, HttpStatusCode.Accepted)]
    [InlineData(172800, HttpStatusCode.Accepted)]
    [InlineData(0, HttpStatusCode.BadRequest)]
    [InlineData(172801, HttpStatusCode.BadRequest)]
    public async Task Create_enforces_the_time_target_numeric_bounds(double targetSeconds, HttpStatusCode expected)
    {
        await using var app = CreateRiderApp()
            .WithSetting("PacingStrategies:Enabled", "true")
            .WithSetting("PacingStrategies:TimeTarget", "true");
        var predictionId = await SeedSucceededBaselineAsync(app.Services);
        using var client = app.CreateClient();

        using var response = await client.PostAsJsonAsync<PacingStrategyRequest>(
            $"/api/predictions/{predictionId}/adjustments",
            new TimeTargetRequest(targetSeconds, "proportional", null, false));

        Assert.Equal(expected, response.StatusCode);
    }

    // Break caught: an unrecognized sub-field literal is silently coerced to a default instead of
    // being reported, so a typo quietly changes the pacing that gets computed.
    [Theory]
    [InlineData("np-if-mode", "{\"type\":\"np-if-target\",\"targetIntensityFactor\":0.85,\"ftpWatts\":250,\"mode\":\"sideways\"}", "mode")]
    [InlineData("time-target-distribution", "{\"type\":\"time-target\",\"targetMovingSeconds\":3600,\"distribution\":\"sideways\",\"includeFeasibilityReport\":false}", "distribution")]
    public async Task Create_rejects_an_unknown_sub_field_literal(string flagCase, string body, string field)
    {
        var flag = flagCase.StartsWith("np-if", StringComparison.Ordinal) ? "NpIfTarget" : "TimeTarget";
        await using var app = CreateRiderApp()
            .WithSetting("PacingStrategies:Enabled", "true")
            .WithSetting($"PacingStrategies:{flag}", "true");
        var predictionId = await SeedSucceededBaselineAsync(app.Services);
        using var client = app.CreateClient();

        using var response = await client.PostAsync(
            $"/api/predictions/{predictionId}/adjustments",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // Break caught: turning a strategy's flag off hides adjustments already stored under it, so an
    // operator rolling a strategy back destroys their users' history from the read side.
    [Fact]
    public async Task Stored_adjustments_remain_readable_after_their_strategy_flag_is_disabled()
    {
        await using var app = CreateRiderApp()
            .WithSetting("PacingStrategies:TimeTarget", "false");
        var predictionId = await SeedSucceededBaselineAsync(app.Services);
        var adjustmentId = await SeedAdjustmentAsync(app.Services, predictionId);
        using var client = app.CreateClient();

        using var list = await client.GetAsync($"/api/predictions/{predictionId}/adjustments");
        using var detail = await client.GetAsync($"/api/predictions/{predictionId}/adjustments/{adjustmentId}");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Contains(adjustmentId.ToString(), await list.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        using var create = await client.PostAsJsonAsync<PacingStrategyRequest>(
            $"/api/predictions/{predictionId}/adjustments",
            new TimeTargetRequest(3600, "proportional", null, false));

        Assert.Equal(HttpStatusCode.Conflict, create.StatusCode);
    }
}

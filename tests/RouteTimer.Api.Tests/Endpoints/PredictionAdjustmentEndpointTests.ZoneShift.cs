using System.Net;
using System.Net.Http.Json;
using RouteTimer.Contracts.Adjustments;
using RouteTimer.Contracts.Errors;
using Xunit;

namespace RouteTimer.Api.Tests.Endpoints;

public partial class PredictionAdjustmentEndpointTests
{
    [Fact]
    public async Task CreateAdjustment_with_valid_zone_shift_returns_202_when_enabled()
    {
        await using var app = CreateRiderApp()
            .WithSetting("PacingStrategies:Enabled", "true")
            .WithSetting("PacingStrategies:RpeZoneShift", "true");
        var predictionId = await SeedSucceededBaselineAsync(app.Services);
        using var client = app.CreateClient();

        var request = new RpeZoneShiftRequest("model-inferred", null, [
            new ZoneAssignmentRequest(true, null, null, 3, "midpoint")
        ]);

        using var response = await client.PostAsJsonAsync<PacingStrategyRequest>(
            $"/api/predictions/{predictionId}/adjustments",
            request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var submission = await response.Content.ReadFromJsonAsync<PredictionAdjustmentSubmissionResponse>();
        Assert.NotNull(submission);
    }
}

using RouteTimer.Services.Activities;
using RouteTimer.Services.Validation;

namespace RouteTimer.Services.Tests.Activities;

public sealed class FitActivityParserTests
{
    [Fact]
    public async Task Parse_reads_power_position_speed_and_timer_state()
    {
        await using var fit = FitTestFileBuilder.ActivityWithPause();

        var result = await new FitActivityParser().ParseAsync(fit, CancellationToken.None);

        Assert.Equal(ActivitySport.Cycling, result.Sport);
        Assert.Contains(result.Samples, sample => sample.PowerWatts == 220 && sample.TimerRunning);
        Assert.Contains(result.Samples, sample => !sample.TimerRunning);
    }

    [Fact]
    public async Task Parse_returns_session_and_device_metadata()
    {
        await using var fit = FitTestFileBuilder.CyclingActivity(
            startedAt: new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero),
            endedAt: new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero),
            totalDistanceMetres: 25_000,
            totalAscentMetres: 320);

        var parsed = await new FitActivityParser().ParseAsync(fit, CancellationToken.None);

        Assert.Equal(25_000, parsed.DeviceDistanceMetres);
        Assert.Equal(320, parsed.DeviceAscentMetres);
        Assert.True(parsed.EndedAt >= parsed.StartedAt);
        Assert.False(string.IsNullOrWhiteSpace(parsed.DeviceManufacturer));
    }

    [Fact]
    public async Task Parse_uses_latest_sample_when_session_end_is_missing()
    {
        var startedAt = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
        await using var fit = FitTestFileBuilder.CyclingActivity(
            startedAt,
            endedAt: startedAt.AddSeconds(99),
            totalDistanceMetres: 1_000,
            totalAscentMetres: 25,
            includeSessionTimestamp: false,
            recordOffsetsSeconds: [0, 6, 12],
            powersWatts: [220, 210, 205]);

        var parsed = await new FitActivityParser().ParseAsync(fit, CancellationToken.None);

        Assert.Equal(startedAt.AddSeconds(12), parsed.EndedAt);
    }

    [Fact]
    public async Task Parse_throws_invalid_session_time_when_end_precedes_start()
    {
        await using var fit = FitTestFileBuilder.CyclingActivity(
            startedAt: new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero),
            endedAt: new DateTimeOffset(2026, 8, 1, 8, 59, 59, TimeSpan.Zero),
            totalDistanceMetres: 1_000,
            totalAscentMetres: 25);

        var exception = await Assert.ThrowsAsync<ActivityInputException>(() => new FitActivityParser().ParseAsync(fit, CancellationToken.None));

        Assert.Equal("invalid-session-time", exception.Code);
    }
}

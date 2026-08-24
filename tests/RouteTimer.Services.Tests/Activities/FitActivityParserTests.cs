using RouteTimer.Services.Activities;

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
}

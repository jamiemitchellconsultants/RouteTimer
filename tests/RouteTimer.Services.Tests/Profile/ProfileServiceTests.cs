using RouteTimer.Services.Profile;

namespace RouteTimer.Services.Tests.Profile;

public sealed class ProfileServiceTests
{
    [Theory]
    [InlineData(0, 10)]
    [InlineData(75, 0)]
    public async Task Profile_rejects_non_positive_weight(double riderKg, double bikeKg)
    {
        var service = new ProfileService();

        await Assert.ThrowsAsync<ProfileValidationException>(() => service.UpdateAsync(riderKg, bikeKg, CancellationToken.None));
    }
}

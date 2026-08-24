using RouteTimer.Domain.Profile;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Profile;

namespace RouteTimer.Services.Tests.Profile;

public sealed class ProfileServiceTests
{
    [Theory]
    [InlineData(0, 10)]
    [InlineData(75, 0)]
    public async Task Profile_rejects_non_positive_weight(double riderKg, double bikeKg)
    {
        var service = new ProfileService(new InMemoryProfileRepository());

        await Assert.ThrowsAsync<ProfileValidationException>(() => service.UpdateAsync(riderKg, bikeKg, CancellationToken.None));
    }

    [Fact]
    public async Task Saved_profile_can_be_read_by_a_fresh_service_instance()
    {
        var repository = new InMemoryProfileRepository();
        var writer = new ProfileService(repository);
        await writer.UpdateAsync(75, 10, CancellationToken.None);
        var reader = new ProfileService(repository);

        var profile = await reader.GetAsync(CancellationToken.None);

        Assert.Equal(new RiderProfile(75, 10), profile);
    }

    private sealed class InMemoryProfileRepository : IProfileRepository
    {
        private RiderProfile? profile;

        public Task<RiderProfile?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(profile);

        public Task SaveAsync(RiderProfile profile, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.profile = profile;
            return Task.CompletedTask;
        }
    }
}

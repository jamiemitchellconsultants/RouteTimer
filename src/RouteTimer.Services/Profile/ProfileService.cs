using RouteTimer.Domain.Profile;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Profile;

public sealed class ProfileValidationException(string message) : Exception(message);

public sealed class ProfileService
{
    private readonly IProfileRepository repository;

    public ProfileService(IProfileRepository repository)
    {
        this.repository = repository;
    }

    public async Task<RiderProfile> UpdateAsync(double riderWeightKg, double bikeAndEquipmentWeightKg, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(riderWeightKg) || riderWeightKg < 30 || riderWeightKg > 250)
        {
            throw new ProfileValidationException("Rider weight must be between 30 and 250 kg.");
        }

        if (!double.IsFinite(bikeAndEquipmentWeightKg) || bikeAndEquipmentWeightKg < 3 || bikeAndEquipmentWeightKg > 60)
        {
            throw new ProfileValidationException("Bike and equipment weight must be between 3 and 60 kg.");
        }

        var profile = new RiderProfile(riderWeightKg, bikeAndEquipmentWeightKg);
        await repository.SaveAsync(profile, cancellationToken);
        return profile;
    }

    public Task<RiderProfile?> GetAsync(CancellationToken cancellationToken) => repository.GetAsync(cancellationToken);
}

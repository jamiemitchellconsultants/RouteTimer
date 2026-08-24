using Microsoft.EntityFrameworkCore;
using RouteTimer.Domain.Profile;
using RouteTimer.Persistence.Entities;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Persistence.Repositories;

public sealed class ProfileRepository(RouteTimerDbContext context) : IProfileRepository
{
    public async Task<RiderProfile?> GetAsync(CancellationToken cancellationToken)
    {
        var profile = await context.Profiles.SingleOrDefaultAsync(cancellationToken);
        return profile is null ? null : new RiderProfile(profile.RiderWeightKg, profile.BikeAndEquipmentWeightKg);
    }

    public async Task SaveAsync(RiderProfile profile, CancellationToken cancellationToken)
    {
        var entity = await context.Profiles.SingleOrDefaultAsync(cancellationToken);
        if (entity is null)
        {
            context.Profiles.Add(new RiderProfileEntity
            {
                Id = 1,
                RiderWeightKg = profile.RiderWeightKg,
                BikeAndEquipmentWeightKg = profile.BikeAndEquipmentWeightKg,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            entity.RiderWeightKg = profile.RiderWeightKg;
            entity.BikeAndEquipmentWeightKg = profile.BikeAndEquipmentWeightKg;
            entity.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}

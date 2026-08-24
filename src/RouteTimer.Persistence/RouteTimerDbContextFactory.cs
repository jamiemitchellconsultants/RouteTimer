using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RouteTimer.Persistence;

/// <summary>Creates the context for EF tooling without starting the API host or its background worker.</summary>
public sealed class RouteTimerDbContextFactory : IDesignTimeDbContextFactory<RouteTimerDbContext>
{
    public RouteTimerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RouteTimerDbContext>()
            .UseNpgsql("Host=localhost;Database=routetimer;Username=routetimer;Password=routetimer")
            .Options;
        return new RouteTimerDbContext(options);
    }
}

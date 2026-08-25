using Microsoft.EntityFrameworkCore;
using RouteTimer.Persistence.Entities;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Persistence.Repositories;

public sealed class LocalCredentialRepository(RouteTimerDbContext context) : ILocalCredentialRepository
{
    // GetAsync deliberately reads without a predicate on this id: SingleOrDefaultAsync then throws if a
    // stray second row ever exists, instead of silently picking one. Do not "simplify" this to
    // FirstOrDefaultAsync(e => e.Id == SingletonId) — that would hide corruption on a security-relevant read.
    private const int SingletonId = 1;

    public async Task<string?> GetAsync(CancellationToken cancellationToken)
    {
        var credential = await context.LocalCredentials.SingleOrDefaultAsync(cancellationToken);
        return credential?.PasswordHash;
    }

    public async Task SetAsync(string passwordHash, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        var now = DateTimeOffset.UtcNow;
        var credential = await context.LocalCredentials.SingleOrDefaultAsync(cancellationToken);
        if (credential is null)
        {
            context.LocalCredentials.Add(new LocalCredentialEntity
            {
                Id = SingletonId,
                PasswordHash = passwordHash,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            credential.PasswordHash = passwordHash;
            credential.UpdatedAt = now;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> TryAddAsync(string passwordHash, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        // Deliberately does not read-then-decide: SetAsync's read-then-upsert shape is exactly what
        // let a concurrent setup loser silently overwrite the winner's passphrase (the loser's own
        // read ran before the winner's write landed, so it took the UPDATE branch instead of hitting
        // a conflict). Going straight to INSERT makes the database's primary key -- backed by
        // CK_local_credential_singleton pinning Id = 1 -- the sole arbiter of "does a credential
        // already exist", with no window for a stale read to get it wrong.
        var now = DateTimeOffset.UtcNow;
        var entity = new LocalCredentialEntity
        {
            Id = SingletonId,
            PasswordHash = passwordHash,
            CreatedAt = now,
            UpdatedAt = now
        };
        context.LocalCredentials.Add(entity);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // Another writer's row already occupies Id = 1. Detach the failed insert so this
            // context instance is not left tracking a phantom row that would collide with the real
            // one -- tracked by the same key -- on the next query through this same DbContext.
            context.Entry(entity).State = EntityState.Detached;
            return false;
        }
    }
}

using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Garmin;

public sealed record GarminConnectionResult(
    string State,
    string? GarminUserId,
    string? DisplayName,
    string? ChallengeId);

public sealed class GarminConnectionService(
    IGarminAdapterClient adapter,
    IGarminConnectionRepository repository,
    IGarminTokenProtector protector,
    GarminOperationGate gate,
    TimeProvider timeProvider)
{
    public Task<GarminConnectionResult> LoginAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            throw new GarminCredentialsRejectedException();
        }

        return gate.RunAsync(async token =>
        {
            var login = await adapter.LoginAsync(email, password, token);
            if (login.State == "mfa-required")
            {
                if (string.IsNullOrWhiteSpace(login.ChallengeId))
                {
                    throw new GarminResponseInvalidException();
                }

                return new GarminConnectionResult("mfa-required", null, null, login.ChallengeId);
            }

            return await SaveConnectedAsync(login);
        }, cancellationToken);
    }

    public Task<GarminConnectionResult> CompleteMfaAsync(
        string challengeId,
        string code,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(challengeId))
        {
            throw new GarminChallengeExpiredException();
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new GarminMfaInvalidException();
        }

        return gate.RunAsync(async token =>
        {
            var login = await adapter.CompleteMfaAsync(challengeId, code, token);
            return await SaveConnectedAsync(login);
        }, cancellationToken);
    }

    public Task<GarminConnectionResult> ValidateAsync(CancellationToken cancellationToken) =>
        gate.RunAsync(async token =>
        {
            var connection = await repository.GetAsync(token);
            if (connection is null)
            {
                return new GarminConnectionResult("disconnected", null, null, null);
            }

            if (connection.State == "reconnect-required")
            {
                return ToResult(connection);
            }

            if (connection.State != "connected")
            {
                throw new GarminResponseInvalidException();
            }

            var tokenJson = protector.Unprotect(connection.Token);
            GarminAdapterSession session;
            try
            {
                session = await adapter.ValidateAsync(tokenJson, token);
            }
            catch (GarminAdapterException exception) when (exception.Error == GarminAdapterError.Authentication)
            {
                await repository.SaveAsync(
                    connection with { State = "reconnect-required", UpdatedAt = timeProvider.GetUtcNow() },
                    CancellationToken.None);
                throw new GarminReconnectRequiredException();
            }

            if (string.IsNullOrWhiteSpace(session.TokenJson))
            {
                throw new GarminResponseInvalidException();
            }

            var now = timeProvider.GetUtcNow();
            var refreshed = new GarminConnectionRecord(
                "connected",
                session.GarminUserId ?? connection.GarminUserId,
                session.DisplayName ?? connection.DisplayName,
                protector.Protect(session.TokenJson),
                now,
                now);
            await repository.SaveAsync(refreshed, CancellationToken.None);
            return ToResult(refreshed);
        }, cancellationToken);

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        _ = await gate.RunAsync(async _ =>
        {
            try
            {
                await adapter.ClearChallengesAsync(cancellationToken);
            }
            catch (Exception)
            {
                // Challenge invalidation is best-effort; deleting the saved connection is mandatory.
            }
            finally
            {
                await repository.DeleteAsync(CancellationToken.None);
            }

            return true;
        }, CancellationToken.None);
    }

    private async Task<GarminConnectionResult> SaveConnectedAsync(GarminAdapterLogin login)
    {
        if (login.State != "connected" || string.IsNullOrWhiteSpace(login.TokenJson))
        {
            throw new GarminResponseInvalidException();
        }

        var now = timeProvider.GetUtcNow();
        var connection = new GarminConnectionRecord(
            "connected",
            login.GarminUserId,
            login.DisplayName,
            protector.Protect(login.TokenJson),
            now,
            now);
        await repository.SaveAsync(connection, CancellationToken.None);
        return ToResult(connection);
    }

    private static GarminConnectionResult ToResult(GarminConnectionRecord connection) =>
        new(connection.State, connection.GarminUserId, connection.DisplayName, null);
}

public sealed class GarminCredentialsRejectedException()
    : Exception("Garmin email and password are required.");

public sealed class GarminMfaInvalidException()
    : Exception("A Garmin MFA code is required.");

public sealed class GarminChallengeExpiredException()
    : Exception("A Garmin MFA challenge is required.");

public sealed class GarminReconnectRequiredException()
    : Exception("The Garmin connection must be established again.");

public sealed class GarminResponseInvalidException()
    : Exception("Garmin returned an unusable response.");

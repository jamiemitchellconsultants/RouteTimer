using System.Globalization;
using System.Text;
using RouteTimer.Services.Persistence;

namespace RouteTimer.Services.Garmin;

public sealed record GarminActivitySummary(
    string ActivityId,
    string Name,
    DateTimeOffset StartedAt,
    string ActivityType,
    double? DistanceMetres,
    double? DurationSeconds,
    double? AscentMetres,
    double? AveragePowerWatts,
    bool AlreadyImported);

public sealed record GarminActivityPage(
    IReadOnlyList<GarminActivitySummary> Activities,
    string? NextCursor);

public sealed class GarminActivityService(
    IGarminAdapterClient adapter,
    IGarminConnectionRepository connections,
    IGarminActivityImportRepository imports,
    IGarminTokenProtector protector,
    GarminOperationGate gate,
    TimeProvider timeProvider)
{
    public Task<GarminActivityPage> GetActivitiesAsync(
        string? cursor,
        CancellationToken cancellationToken)
    {
        var offset = GarminCursor.Decode(cursor);
        return gate.RunAsync(async token =>
        {
            var connection = await RequireConnectedAsync(token);
            var tokenJson = protector.Unprotect(connection.Token);
            GarminAdapterActivityPage adapterPage;
            try
            {
                adapterPage = await adapter.GetActivitiesAsync(tokenJson, offset, token);
            }
            catch (GarminAdapterException exception) when (exception.Error == GarminAdapterError.Authentication)
            {
                await connections.SaveAsync(
                    connection with { State = "reconnect-required", UpdatedAt = timeProvider.GetUtcNow() },
                    CancellationToken.None);
                throw new GarminReconnectRequiredException();
            }

            if (string.IsNullOrWhiteSpace(adapterPage.TokenJson))
            {
                throw new GarminResponseInvalidException();
            }

            if (!string.Equals(tokenJson, adapterPage.TokenJson, StringComparison.Ordinal))
            {
                var now = timeProvider.GetUtcNow();
                await connections.SaveAsync(
                    connection with
                    {
                        Token = protector.Protect(adapterPage.TokenJson),
                        LastValidatedAt = now,
                        UpdatedAt = now
                    },
                    CancellationToken.None);
            }

            var allowed = adapterPage.Activities
                .Where(static activity => activity.ActivityType is "road-cycling" or "gravel-cycling")
                .ToArray();
            var linkedIds = await imports.GetLinkedIdsAsync(
                allowed.Select(static activity => activity.ActivityId).ToArray(),
                token);
            var activities = allowed
                .Select(activity => new GarminActivitySummary(
                    activity.ActivityId,
                    activity.Name,
                    activity.StartedAt,
                    activity.ActivityType,
                    activity.DistanceMetres,
                    activity.DurationSeconds,
                    activity.AscentMetres,
                    activity.AveragePowerWatts,
                    linkedIds.Contains(activity.ActivityId)))
                .ToArray();
            return new GarminActivityPage(activities, GarminCursor.Encode(adapterPage.NextOffset));
        }, cancellationToken);
    }

    private async Task<GarminConnectionRecord> RequireConnectedAsync(CancellationToken cancellationToken)
    {
        var connection = await connections.GetAsync(cancellationToken);
        if (connection is null)
        {
            throw new GarminConnectionRequiredException();
        }

        return connection.State switch
        {
            "connected" => connection,
            "reconnect-required" => throw new GarminReconnectRequiredException(),
            _ => throw new GarminResponseInvalidException()
        };
    }
}

internal static class GarminCursor
{
    private const int MaximumOffset = 100_000_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static int Decode(string? cursor)
    {
        if (cursor is null)
        {
            return 0;
        }

        try
        {
            if (cursor.Length == 0 || cursor.Any(static character =>
                    !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            {
                throw new GarminCursorInvalidException();
            }

            var paddingLength = (4 - cursor.Length % 4) % 4;
            var padded = cursor.Replace('-', '+').Replace('_', '/') + new string('=', paddingLength);
            var bytes = Convert.FromBase64String(padded);
            var decimalOffset = StrictUtf8.GetString(bytes);
            if (!int.TryParse(decimalOffset, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ||
                offset is < 0 or > MaximumOffset ||
                !string.Equals(decimalOffset, offset.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
                !string.Equals(cursor, Encode(offset), StringComparison.Ordinal))
            {
                throw new GarminCursorInvalidException();
            }

            return offset;
        }
        catch (GarminCursorInvalidException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw new GarminCursorInvalidException();
        }
    }

    public static string? Encode(int? offset)
    {
        if (offset is null)
        {
            return null;
        }

        if (offset is < 0 or > MaximumOffset)
        {
            throw new GarminResponseInvalidException();
        }

        var bytes = Encoding.UTF8.GetBytes(offset.Value.ToString(CultureInfo.InvariantCulture));
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

public sealed class GarminConnectionRequiredException()
    : Exception("A connected Garmin account is required.");

public sealed class GarminCursorInvalidException()
    : Exception("The Garmin activity cursor is invalid.");

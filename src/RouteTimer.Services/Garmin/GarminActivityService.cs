using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Training;

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

public sealed record GarminImportResult(
    string ActivityId,
    string? Name,
    string Outcome,
    Guid? UploadId,
    Guid? JobId,
    string? ErrorCode);

public sealed class GarminActivityService(
    IGarminAdapterClient adapter,
    IGarminConnectionRepository connections,
    IGarminActivityImportRepository imports,
    IGarminTokenProtector protector,
    GarminOperationGate gate,
    TrainingUploadService uploads,
    TimeProvider timeProvider,
    ILogger<GarminActivityService> logger)
{
    private const int MaximumImportCount = 10;
    private const int MaximumActivityIdLength = 64;
    private const int MaximumFileNameLength = 512;

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

    public Task<IReadOnlyList<GarminImportResult>> ImportAsync(
        IReadOnlyList<string> activityIds,
        CancellationToken cancellationToken)
    {
        ValidateSelection(activityIds);
        var requestedIds = activityIds.ToArray();
        return gate.RunAsync(
            token => ImportBatchAsync(requestedIds, token),
            cancellationToken);
    }

    private async Task<IReadOnlyList<GarminImportResult>> ImportBatchAsync(
        IReadOnlyList<string> activityIds,
        CancellationToken cancellationToken)
    {
        var connection = await RequireConnectedAsync(cancellationToken);
        var tokenJson = protector.Unprotect(connection.Token);
        var results = new List<GarminImportResult>(activityIds.Count);

        foreach (var activityId in activityIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = await imports.GetAsync(activityId, cancellationToken);
            if (existing is not null)
            {
                results.Add(new GarminImportResult(
                    activityId,
                    existing.ActivityName,
                    "already-imported",
                    existing.UploadId,
                    existing.JobId,
                    null));
                continue;
            }

            GarminAdapterActivityResult summaryResult;
            try
            {
                summaryResult = await adapter.GetActivityAsync(tokenJson, activityId, cancellationToken);
                (connection, tokenJson) = await PersistRotationAsync(
                    connection,
                    tokenJson,
                    summaryResult.TokenJson);
            }
            catch (GarminAdapterException exception)
            {
                await MarkReconnectRequiredAsync(exception, connection);
                results.Add(AdapterFailure(activityId, null, exception));
                continue;
            }
            catch (GarminResponseInvalidException)
            {
                results.Add(InvalidResult(activityId, null, "garmin-response-invalid"));
                continue;
            }

            var summary = summaryResult.Activity;
            var safeName = SafeDisplayName(summary.Name, activityId);

            // Deliberately not re-checking summary.ActivityType here: Garmin's list endpoint (which
            // already filtered to road/gravel cycling to offer this activity for import) and its
            // single-activity detail endpoint are separate backend services that can disagree on an
            // activity's type for reasons unrelated to anything the rider did -- confirmed directly
            // against a real account, where an activity the list reported as road_biking came back
            // with a different, unmapped type from the detail endpoint days later. Re-validating type
            // here would reject an activity the rider already deliberately selected from that
            // filtered list, on the word of a second Garmin service that isn't more authoritative
            // than the first. The adapter mirrors this: get_activity (used only here) no longer
            // requires a recognised type; list_activities still does.
            if (summary.ActivityId.Length is < 1 or > MaximumActivityIdLength ||
                !string.Equals(summary.ActivityId, activityId, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "Garmin activity summary returned a different activity than requested: requestedId={RequestedId} returnedId={ReturnedId}",
                    activityId, summary.ActivityId);
                results.Add(InvalidResult(activityId, safeName, "garmin-response-invalid"));
                continue;
            }

            GarminAdapterFitDownload download;
            try
            {
                download = await adapter.DownloadFitAsync(tokenJson, activityId, cancellationToken);
            }
            catch (GarminAdapterException exception)
            {
                await MarkReconnectRequiredAsync(exception, connection);
                results.Add(AdapterFailure(activityId, safeName, exception));
                continue;
            }

            await using (download)
            {
                try
                {
                    (connection, tokenJson) = await PersistRotationAsync(
                        connection,
                        tokenJson,
                        download.TokenJson);
                }
                catch (GarminResponseInvalidException)
                {
                    results.Add(InvalidResult(activityId, safeName, "garmin-response-invalid"));
                    continue;
                }

                TrainingUploadResult uploadResult;
                try
                {
                    uploadResult = AssertSingle(await uploads.AcceptAsync(
                        [new TrainingUpload(
                            BuildFileName(summary.Name, activityId),
                            download.Content,
                            new GarminActivitySource(activityId, safeName))],
                        cancellationToken));
                }
                catch (TrainingUploadReadException)
                {
                    results.Add(new GarminImportResult(
                        activityId,
                        safeName,
                        "download-failed",
                        null,
                        null,
                        "garmin-unavailable"));
                    continue;
                }

                results.Add(ToImportResult(activityId, safeName, uploadResult));
            }
        }

        return results;
    }

    private async Task<(GarminConnectionRecord Connection, string TokenJson)> PersistRotationAsync(
        GarminConnectionRecord connection,
        string previousTokenJson,
        string rotatedTokenJson)
    {
        if (string.IsNullOrWhiteSpace(rotatedTokenJson))
        {
            throw new GarminResponseInvalidException();
        }

        if (string.Equals(previousTokenJson, rotatedTokenJson, StringComparison.Ordinal))
        {
            return (connection, previousTokenJson);
        }

        var now = timeProvider.GetUtcNow();
        var updated = connection with
        {
            Token = protector.Protect(rotatedTokenJson),
            LastValidatedAt = now,
            UpdatedAt = now
        };
        await connections.SaveAsync(updated, CancellationToken.None);
        return (updated, rotatedTokenJson);
    }

    private async Task MarkReconnectRequiredAsync(
        GarminAdapterException exception,
        GarminConnectionRecord connection)
    {
        if (exception.Error != GarminAdapterError.Authentication)
        {
            return;
        }

        await connections.SaveAsync(
            connection with { State = "reconnect-required", UpdatedAt = timeProvider.GetUtcNow() },
            CancellationToken.None);
    }

    private static GarminImportResult AdapterFailure(
        string activityId,
        string? name,
        GarminAdapterException exception) =>
        exception.Error switch
        {
            GarminAdapterError.ActivityNotAllowed or
            GarminAdapterError.RequestInvalid or
            GarminAdapterError.ResponseInvalid => InvalidResult(activityId, name, "garmin-response-invalid"),
            GarminAdapterError.FitTooLarge => InvalidResult(activityId, name, "invalid-fit-upload"),
            _ => new GarminImportResult(
                activityId,
                name,
                "download-failed",
                null,
                null,
                exception.Error switch
                {
                    GarminAdapterError.Authentication => "garmin-reconnect-required",
                    GarminAdapterError.RateLimited => "garmin-rate-limited",
                    GarminAdapterError.Unavailable => "garmin-unavailable",
                    GarminAdapterError.AdapterUnavailable => "garmin-adapter-unavailable",
                    _ => "garmin-response-invalid"
                })
        };

    private static GarminImportResult InvalidResult(string activityId, string? name, string errorCode) =>
        new(activityId, name, "invalid-fit", null, null, errorCode);

    private static GarminImportResult ToImportResult(
        string activityId,
        string name,
        TrainingUploadResult result) =>
        result.AcceptanceOutcome switch
        {
            TrainingUploadAcceptanceOutcome.Accepted => new GarminImportResult(
                activityId, name, "accepted", result.UploadId, result.JobId, null),
            TrainingUploadAcceptanceOutcome.AlreadyImported => new GarminImportResult(
                activityId, name, "already-imported", result.UploadId, result.JobId, null),
            TrainingUploadAcceptanceOutcome.DuplicateHash => new GarminImportResult(
                activityId, name, "duplicate", result.UploadId, result.JobId, result.ErrorCode),
            _ when result.Outcome == UploadOutcome.Invalid => InvalidResult(
                activityId, name, result.ErrorCode ?? "invalid-fit-upload"),
            _ => InvalidResult(activityId, name, "garmin-response-invalid")
        };

    private static TrainingUploadResult AssertSingle(IReadOnlyList<TrainingUploadResult> results) =>
        results.Count == 1 ? results[0] : throw new GarminResponseInvalidException();

    private static void ValidateSelection(IReadOnlyList<string> activityIds)
    {
        if (activityIds is null ||
            activityIds.Count is < 1 or > MaximumImportCount ||
            activityIds.Any(string.IsNullOrWhiteSpace) ||
            activityIds.Distinct(StringComparer.Ordinal).Count() != activityIds.Count)
        {
            throw new GarminImportLimitException();
        }
    }

    private static string SafeDisplayName(string? name, string activityId)
    {
        var safe = new string((name ?? string.Empty)
            .Where(static character => !char.IsControl(character))
            .ToArray()).Trim();
        if (safe.Length == 0)
        {
            safe = $"Garmin activity {activityId}";
        }

        return safe.Length <= MaximumFileNameLength ? safe : safe[..MaximumFileNameLength];
    }

    private static string BuildFileName(string? activityName, string activityId)
    {
        var safeId = SanitizeFileComponent(activityId);
        if (safeId.Length == 0)
        {
            safeId = "activity";
        }

        var maximumIdLength = MaximumFileNameLength - "garmin-.fit".Length;
        if (safeId.Length > maximumIdLength)
        {
            safeId = safeId[..maximumIdLength];
        }

        var suffix = $"-{safeId}.fit";
        var safeName = SanitizeFileComponent(activityName);
        if (safeName.Length == 0)
        {
            safeName = "garmin";
        }

        var maximumNameLength = MaximumFileNameLength - suffix.Length;
        if (safeName.Length > maximumNameLength)
        {
            safeName = safeName[..maximumNameLength];
        }

        safeName = safeName.Trim(' ', '.', '-');
        return $"{(safeName.Length == 0 ? "garmin" : safeName)}{suffix}";
    }

    private static string SanitizeFileComponent(string? value) =>
        new((value ?? string.Empty)
            .Where(static character =>
                !char.IsControl(character) &&
                character is not '/' and not '\\' and not '<' and not '>' and not ':' and not '"' and not '|' and not '?' and not '*')
            .ToArray());

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

public sealed class GarminImportLimitException()
    : Exception("Select between one and ten distinct Garmin activities.");

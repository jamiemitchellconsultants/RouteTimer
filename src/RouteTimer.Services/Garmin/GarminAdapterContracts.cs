namespace RouteTimer.Services.Garmin;

public interface IGarminAdapterClient
{
    Task<GarminAdapterLogin> LoginAsync(string email, string password, CancellationToken cancellationToken);
    Task<GarminAdapterLogin> CompleteMfaAsync(string challengeId, string code, CancellationToken cancellationToken);
    Task<GarminAdapterSession> ValidateAsync(string tokenJson, CancellationToken cancellationToken);
    Task<GarminAdapterActivityPage> GetActivitiesAsync(string tokenJson, int offset, CancellationToken cancellationToken);
    Task<GarminAdapterActivityResult> GetActivityAsync(string tokenJson, string activityId, CancellationToken cancellationToken);
    Task<GarminAdapterFitDownload> DownloadFitAsync(string tokenJson, string activityId, CancellationToken cancellationToken);
    Task<GarminAdapterCourse> CreateCourseAsync(string tokenJson, GarminCourseRequest request, CancellationToken cancellationToken);
    Task ClearChallengesAsync(CancellationToken cancellationToken);
}

public sealed record GarminAdapterLogin(
    string State,
    string? ChallengeId,
    string? TokenJson,
    string? GarminUserId,
    string? DisplayName);

public sealed record GarminAdapterSession(string TokenJson, string? GarminUserId, string? DisplayName);

public sealed record GarminAdapterActivity(
    string ActivityId,
    string Name,
    DateTimeOffset StartedAt,
    string ActivityType,
    double? DistanceMetres,
    double? DurationSeconds,
    double? AscentMetres,
    double? AveragePowerWatts);

public sealed record GarminAdapterActivityPage(
    IReadOnlyList<GarminAdapterActivity> Activities,
    int? NextOffset,
    string TokenJson);

public sealed record GarminAdapterActivityResult(GarminAdapterActivity Activity, string TokenJson);

public sealed record GarminAdapterFitDownload(string FileName, Stream Content, string TokenJson) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public sealed record GarminCourseRequest(
    string FileName,
    string CourseName,
    string ActivityType,
    string? Description,
    double ElevationGainMetres,
    double ElevationLossMetres,
    byte[] Gpx);

public sealed record GarminAdapterCourse(long CourseId, string CourseName, string TokenJson);

public enum GarminAdapterError
{
    CredentialsRejected,
    MfaInvalid,
    Authentication,
    ChallengeExpired,
    RateLimited,
    Unavailable,
    AdapterUnavailable,
    ResponseInvalid,
    RequestInvalid,
    ActivityNotAllowed,
    FitTooLarge,
    CourseRejected
}

public sealed class GarminAdapterException(GarminAdapterError error, string message) : Exception(message)
{
    public GarminAdapterError Error { get; } = error;
}

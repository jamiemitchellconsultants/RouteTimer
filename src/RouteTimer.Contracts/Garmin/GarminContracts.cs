namespace RouteTimer.Contracts.Garmin;

public sealed record GarminLoginRequest(string Email, string Password)
{
    public override string ToString() => "GarminLoginRequest { Email = <redacted>, Password = <redacted> }";
}

public sealed record GarminMfaRequest(string ChallengeId, string Code)
{
    public override string ToString() => "GarminMfaRequest { ChallengeId = <redacted>, Code = <redacted> }";
}

public sealed record GarminConnectionResponse(
    string State,
    string? GarminUserId,
    string? DisplayName,
    string? ChallengeId);

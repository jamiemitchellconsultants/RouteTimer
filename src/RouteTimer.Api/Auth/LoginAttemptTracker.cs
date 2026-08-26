namespace RouteTimer.Api.Auth;

/// <summary>
/// Counts failed sign-in attempts so lockout is driven by verification outcomes rather than by
/// request volume.
/// </summary>
/// <remarks>
/// The rate limiter cannot do this job. <c>RequireRateLimiting</c> is middleware: it consumes a
/// permit before the endpoint runs, so it counts requests without ever learning whether the
/// passphrase was wrong. Driving lockout off that would let anonymous probes made before first-run
/// setup exhaust the budget and lock a rider out of their own first genuine sign-in, which is the
/// specific failure this type exists to prevent. Only a wrong guess against a real credential is
/// recorded here; a probe before setup, or against a corrupt row, is not.
/// </remarks>
public sealed class LoginAttemptTracker(TimeProvider timeProvider)
{
    /// <summary>
    /// Wrong guesses tolerated within <see cref="Window"/>. PBKDF2 at the framework's default cost
    /// admits maybe ten to twenty guesses a second unthrottled, so ten a minute is already a
    /// reduction of about three orders of magnitude -- far more than a twelve-character minimum
    /// needs. The remaining headroom buys back the rider who mistypes a long passphrase, who pays
    /// for a lockout with a hard block on the correct passphrase too.
    /// </summary>
    public const int MaximumFailuresPerWindow = 10;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Lock gate = new();
    private int failures;
    private DateTimeOffset windowStart = DateTimeOffset.MinValue;

    /// <summary>Whether sign-in is currently locked out, and for how much longer.</summary>
    public bool IsLockedOut(out TimeSpan retryAfter)
    {
        lock (gate)
        {
            var elapsed = timeProvider.GetUtcNow() - windowStart;
            if (elapsed >= Window)
            {
                retryAfter = TimeSpan.Zero;
                return false;
            }

            retryAfter = Window - elapsed;
            return failures >= MaximumFailuresPerWindow;
        }
    }

    /// <summary>Records a wrong guess against a real credential. Nothing else may call this.</summary>
    public void RecordFailure()
    {
        lock (gate)
        {
            var now = timeProvider.GetUtcNow();
            if (now - windowStart >= Window)
            {
                windowStart = now;
                failures = 0;
            }

            failures++;
        }
    }

    /// <summary>Clears the count after a successful sign-in.</summary>
    public void Reset()
    {
        lock (gate)
        {
            failures = 0;
            windowStart = DateTimeOffset.MinValue;
        }
    }
}

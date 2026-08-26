using Microsoft.Extensions.Time.Testing;
using RouteTimer.Api.Auth;

namespace RouteTimer.Api.Tests.Auth;

public sealed class LoginAttemptTrackerTests
{
    [Fact]
    public void The_lockout_window_is_short_enough_for_a_rider_to_wait_out()
    {
        // Every other test here advances time by Window itself, so none of them can see a wrong
        // Window -- they would pass just as happily if it were a hundred days, stranding a rider
        // permanently locked out of their own installation. This is the assertion that pins it.
        Assert.Equal(TimeSpan.FromMinutes(1), LoginAttemptTracker.Window);
    }

    [Fact]
    public void A_fresh_tracker_is_not_locked_out()
    {
        var tracker = new LoginAttemptTracker(new FakeTimeProvider());

        Assert.False(tracker.IsLockedOut(out var retryAfter));
        Assert.Equal(TimeSpan.Zero, retryAfter);
    }

    [Fact]
    public void Locks_out_only_once_the_budget_is_exhausted()
    {
        var tracker = new LoginAttemptTracker(new FakeTimeProvider());

        for (var attempt = 0; attempt < LoginAttemptTracker.MaximumFailuresPerWindow - 1; attempt++)
        {
            tracker.RecordFailure();
            Assert.False(tracker.IsLockedOut(out _));
        }

        tracker.RecordFailure();

        Assert.True(tracker.IsLockedOut(out var retryAfter));
        Assert.Equal(LoginAttemptTracker.Window, retryAfter);
    }

    [Fact]
    public void The_lockout_releases_when_the_window_elapses()
    {
        var time = new FakeTimeProvider();
        var tracker = Locked(time);

        // The mutant this exists to kill is a window that never expires, which would strand a rider
        // permanently locked out of their own installation.
        time.Advance(LoginAttemptTracker.Window - TimeSpan.FromMilliseconds(1));
        Assert.True(tracker.IsLockedOut(out var stillLocked));
        Assert.Equal(TimeSpan.FromMilliseconds(1), stillLocked);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(tracker.IsLockedOut(out _));
    }

    [Fact]
    public void A_failure_after_the_window_starts_a_fresh_budget_rather_than_relocking()
    {
        var time = new FakeTimeProvider();
        var tracker = Locked(time);
        time.Advance(LoginAttemptTracker.Window);

        tracker.RecordFailure();

        Assert.False(tracker.IsLockedOut(out _));
    }

    [Fact]
    public void Success_clears_the_budget()
    {
        var time = new FakeTimeProvider();
        var tracker = Locked(time);

        tracker.Reset();

        Assert.False(tracker.IsLockedOut(out _));
    }

    [Fact]
    public void Failures_drip_fed_slower_than_the_window_never_lock_out()
    {
        var time = new FakeTimeProvider();
        var tracker = new LoginAttemptTracker(time);

        for (var attempt = 0; attempt < LoginAttemptTracker.MaximumFailuresPerWindow * 3; attempt++)
        {
            tracker.RecordFailure();
            time.Advance(LoginAttemptTracker.Window);
        }

        Assert.False(tracker.IsLockedOut(out _));
    }

    [Fact]
    public void Concurrent_failures_leave_consistent_state()
    {
        var tracker = new LoginAttemptTracker(new FakeTimeProvider());

        Parallel.For(0, 1000, _ => tracker.RecordFailure());

        Assert.True(tracker.IsLockedOut(out var retryAfter));
        Assert.Equal(LoginAttemptTracker.Window, retryAfter);
    }

    private static LoginAttemptTracker Locked(FakeTimeProvider time)
    {
        var tracker = new LoginAttemptTracker(time);
        for (var attempt = 0; attempt < LoginAttemptTracker.MaximumFailuresPerWindow; attempt++)
        {
            tracker.RecordFailure();
        }

        Assert.True(tracker.IsLockedOut(out _));
        return tracker;
    }
}

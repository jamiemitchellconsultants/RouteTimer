namespace RouteTimer.Client.Logging;

public enum ActionLevel
{
    Info,
    Success,
    Warn,
    Error
}

public sealed record LogEntry(DateTimeOffset At, ActionLevel Level, string Message, string? Detail);

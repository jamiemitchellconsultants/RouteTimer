namespace RouteTimer.Services.Validation;

/// <summary>
/// Common base for job-handler exceptions that represent a permanent failure - one the hosted worker
/// (<c>AnalysisWorker</c>) should report immediately via <see cref="Code"/>/<see cref="Exception.Message"/>
/// rather than retry. Sharing this base lets the worker's exception classification stay a single
/// catch clause as more job types (and their own permanent-failure exception types) are added, instead
/// of growing a special case per exception type.
/// </summary>
public abstract class RouteTimerJobException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

using System.Collections.ObjectModel;
using System.Net;

namespace RouteTimer.Client.Api;

public sealed class ApiProblemException : Exception
{
    private static readonly IReadOnlyDictionary<string, string[]> EmptyErrors =
        new ReadOnlyDictionary<string, string[]>(new Dictionary<string, string[]>(StringComparer.Ordinal));

    public ApiProblemException(
        HttpStatusCode statusCode,
        string code,
        string title,
        string? detail,
        IReadOnlyDictionary<string, string[]>? errors = null)
        : base(title)
    {
        StatusCode = statusCode;
        Code = string.IsNullOrWhiteSpace(code) ? "request-failed" : code;
        Title = string.IsNullOrWhiteSpace(title) ? "Request failed" : title;
        Detail = string.IsNullOrWhiteSpace(detail) ? null : detail;
        Errors = errors is null
            ? EmptyErrors
            : new ReadOnlyDictionary<string, string[]>(
                errors.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToArray(),
                    StringComparer.Ordinal));
    }

    public HttpStatusCode StatusCode { get; }

    public string Code { get; }

    public string Title { get; }

    public string? Detail { get; }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}

using RouteTimer.Contracts.Errors;

namespace RouteTimer.Api.Uploads;

public static class MultipartUploadReader
{
    public static async Task<IFormFileCollection> ReadAsync(
        HttpRequest request,
        int minimumFileCount,
        int maximumFileCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumFileCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFileCount);
        if (minimumFileCount > maximumFileCount)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumFileCount), "The minimum file count cannot exceed the maximum file count.");
        }

        if (!request.HasFormContentType || !HasMultipartBoundary(request.ContentType))
        {
            throw MultipartUploadException.MultipartRequired();
        }

        IFormFileCollection files;
        try
        {
            files = (await request.ReadFormAsync(cancellationToken)).Files;
        }
        catch (BadHttpRequestException exception)
        {
            throw MultipartUploadException.Malformed(exception);
        }
        catch (InvalidDataException exception)
        {
            throw MultipartUploadException.Malformed(exception);
        }
        catch (InvalidOperationException exception) when (LooksLikeMultipartParseFailure(exception))
        {
            throw MultipartUploadException.Malformed(exception);
        }
        catch (ArgumentException exception) when (LooksLikeMultipartParseFailure(exception))
        {
            throw MultipartUploadException.Malformed(exception);
        }
        catch (Exception exception) when (LooksLikeMultipartParseFailure(exception))
        {
            throw MultipartUploadException.Malformed(exception);
        }

        if (files.Count < minimumFileCount || files.Count > maximumFileCount)
        {
            throw new MultipartUploadFileCountException(files.Count, minimumFileCount, maximumFileCount);
        }

        return files;
    }

    private static bool HasMultipartBoundary(string? contentType) =>
        !string.IsNullOrWhiteSpace(contentType) &&
        contentType.Contains("boundary=", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeMultipartParseFailure(Exception exception) =>
        exception.GetType().Name.Contains("BadHttpRequest", StringComparison.OrdinalIgnoreCase) ||
        exception.GetType().Name.Contains("Multipart", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("multipart", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("boundary", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("unexpected end", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("request content", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("end of stream", StringComparison.OrdinalIgnoreCase);

}

public sealed class MultipartUploadException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;

    public static MultipartUploadException MultipartRequired() =>
        new(ErrorCodes.MultipartRequired, "A multipart upload is required.");

    public static MultipartUploadException Malformed(Exception innerException) =>
        new(ErrorCodes.MultipartRequired, "The multipart request is malformed.", innerException);
}

public sealed class MultipartUploadFileCountException(int actualFileCount, int minimumFileCount, int maximumFileCount)
    : Exception("The multipart upload contained an invalid number of files.")
{
    public int ActualFileCount { get; } = actualFileCount;
    public int MinimumFileCount { get; } = minimumFileCount;
    public int MaximumFileCount { get; } = maximumFileCount;
}

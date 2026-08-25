using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using RouteTimer.Services.Garmin;

namespace RouteTimer.Api.Garmin;

public sealed class GarminAdapterClient(HttpClient httpClient) : IGarminAdapterClient
{
    private const string TokenHeader = "X-RouteTimer-Garmin-Token";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public Task<GarminAdapterLogin> LoginAsync(string email, string password, CancellationToken cancellationToken) =>
        SendJsonAsync<GarminAdapterLogin>(HttpMethod.Post, "/v1/auth/login", new { email, password }, ValidateLogin, cancellationToken);

    public Task<GarminAdapterLogin> CompleteMfaAsync(string challengeId, string code, CancellationToken cancellationToken) =>
        SendJsonAsync<GarminAdapterLogin>(HttpMethod.Post, "/v1/auth/mfa", new { challengeId, code }, ValidateLogin, cancellationToken);

    public Task<GarminAdapterSession> ValidateAsync(string tokenJson, CancellationToken cancellationToken) =>
        SendJsonAsync<GarminAdapterSession>(HttpMethod.Post, "/v1/auth/validate", new { token = tokenJson }, ValidateSession, cancellationToken);

    public Task<GarminAdapterActivityPage> GetActivitiesAsync(string tokenJson, int offset, CancellationToken cancellationToken) =>
        SendJsonAsync<GarminAdapterActivityPage>(HttpMethod.Post, "/v1/activities/page", new { token = tokenJson, offset }, ValidatePage, cancellationToken);

    public Task<GarminAdapterActivityResult> GetActivityAsync(string tokenJson, string activityId, CancellationToken cancellationToken) =>
        SendJsonAsync<GarminAdapterActivityResult>(
            HttpMethod.Post,
            $"/v1/activities/{Uri.EscapeDataString(activityId)}/summary",
            new { token = tokenJson },
            ValidateActivityResult,
            cancellationToken);

    public async Task ClearChallengesAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/v1/auth/challenges");
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        if (response.StatusCode != HttpStatusCode.NoContent)
        {
            throw ResponseInvalid();
        }
    }

    public async Task<GarminAdapterFitDownload> DownloadFitAsync(string tokenJson, string activityId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/activities/{Uri.EscapeDataString(activityId)}/fit")
        {
            Content = JsonContent.Create(new { token = tokenJson }, options: JsonOptions)
        };

        HttpResponseMessage? response = null;
        try
        {
            response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            var refreshedToken = DecodeTokenHeader(response);
            var expectedFileName = $"{activityId}.fit";
            var contentDisposition = response.Content.Headers.ContentDisposition;
            var fileName = contentDisposition?.FileNameStar?.Trim('"') ?? contentDisposition?.FileName?.Trim('"');
            if (!string.Equals(fileName, expectedFileName, StringComparison.Ordinal))
            {
                throw ResponseInvalid();
            }

            var content = await response.Content.ReadAsStreamAsync(cancellationToken);
            var download = new GarminAdapterFitDownload(expectedFileName, new ResponseOwningStream(response, content), refreshedToken);
            response = null;
            return download;
        }
        finally
        {
            response?.Dispose();
        }
    }

    private async Task<T> SendJsonAsync<T>(
        HttpMethod method,
        string path,
        object body,
        Func<T, bool> isValid,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        try
        {
            var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
            if (result is null || !isValid(result))
            {
                throw ResponseInvalid();
            }

            return result;
        }
        catch (GarminAdapterException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw ResponseInvalid();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.SendAsync(request, completionOption, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw AdapterUnavailable();
        }
        catch (HttpRequestException)
        {
            throw AdapterUnavailable();
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? code = null;
        try
        {
            var error = await response.Content.ReadFromJsonAsync<AdapterErrorResponse>(JsonOptions, cancellationToken);
            code = error?.Code;
        }
        catch (JsonException)
        {
        }

        throw ErrorFromCode(code);
    }

    private static string DecodeTokenHeader(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(TokenHeader, out var values))
        {
            throw ResponseInvalid();
        }

        var headers = values.ToList();
        if (headers.Count != 1 || string.IsNullOrWhiteSpace(headers[0]))
        {
            throw ResponseInvalid();
        }

        try
        {
            var tokenJson = StrictUtf8.GetString(WebEncoders.Base64UrlDecode(headers[0]));
            using var _ = JsonDocument.Parse(tokenJson);
            return tokenJson;
        }
        catch (DecoderFallbackException)
        {
            throw ResponseInvalid();
        }
        catch (ArgumentException)
        {
            throw ResponseInvalid();
        }
        catch (FormatException)
        {
            throw ResponseInvalid();
        }
        catch (JsonException)
        {
            throw ResponseInvalid();
        }
    }

    private static bool ValidateLogin(GarminAdapterLogin login) =>
        login.State switch
        {
            "connected" => !string.IsNullOrWhiteSpace(login.TokenJson),
            "mfa-required" => !string.IsNullOrWhiteSpace(login.ChallengeId),
            _ => false
        };

    private static bool ValidateSession(GarminAdapterSession session) =>
        !string.IsNullOrWhiteSpace(session.TokenJson);

    private static bool ValidatePage(GarminAdapterActivityPage page) =>
        !string.IsNullOrWhiteSpace(page.TokenJson) &&
        page.Activities is not null &&
        page.Activities.All(IsValidActivity);

    private static bool ValidateActivityResult(GarminAdapterActivityResult result) =>
        !string.IsNullOrWhiteSpace(result.TokenJson) &&
        result.Activity is not null &&
        IsValidActivity(result.Activity);

    private static bool IsValidActivity(GarminAdapterActivity activity) =>
        !string.IsNullOrWhiteSpace(activity.ActivityId) &&
        !string.IsNullOrWhiteSpace(activity.Name) &&
        !string.IsNullOrWhiteSpace(activity.ActivityType) &&
        activity.StartedAt != default;

    private static GarminAdapterException ErrorFromCode(string? code) =>
        code switch
        {
            "credentials-rejected" => new(GarminAdapterError.CredentialsRejected, "Garmin credentials were rejected."),
            "mfa-invalid" => new(GarminAdapterError.MfaInvalid, "The Garmin MFA code was rejected."),
            "authentication" => new(GarminAdapterError.Authentication, "Garmin authentication failed."),
            "challenge-expired" => new(GarminAdapterError.ChallengeExpired, "The Garmin MFA challenge expired."),
            "rate-limited" => new(GarminAdapterError.RateLimited, "Garmin rate limited the request."),
            "unavailable" => new(GarminAdapterError.Unavailable, "Garmin is unavailable."),
            "response-invalid" => ResponseInvalid(),
            "request-invalid" => new(GarminAdapterError.RequestInvalid, "The Garmin adapter rejected the request."),
            "activity-not-allowed" => new(GarminAdapterError.ActivityNotAllowed, "The Garmin activity is not available."),
            "fit-too-large" => new(GarminAdapterError.FitTooLarge, "The Garmin FIT file is too large."),
            _ => ResponseInvalid()
        };

    private static GarminAdapterException AdapterUnavailable() =>
        new(GarminAdapterError.AdapterUnavailable, "The Garmin adapter is unavailable.");

    private static GarminAdapterException ResponseInvalid() =>
        new(GarminAdapterError.ResponseInvalid, "The Garmin adapter returned an invalid response.");

    private sealed record AdapterErrorResponse(string? Code);

    private sealed class ResponseOwningStream(HttpResponseMessage response, Stream content) : Stream
    {
        private bool disposed;

        public override bool CanRead => content.CanRead;
        public override bool CanSeek => content.CanSeek;
        public override bool CanWrite => content.CanWrite;
        public override long Length => content.Length;
        public override long Position { get => content.Position; set => content.Position = value; }
        public override void Flush() => content.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => content.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => content.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => content.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => content.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => content.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => content.Seek(offset, origin);
        public override void SetLength(long value) => content.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => content.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => content.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => content.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => content.WriteAsync(buffer, offset, count, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                response.Dispose();
            }

            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

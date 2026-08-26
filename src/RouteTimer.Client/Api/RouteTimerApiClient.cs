using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RouteTimer.Contracts.Jobs;
using RouteTimer.Contracts.Models;
using RouteTimer.Contracts.Predictions;
using RouteTimer.Contracts.Profile;
using RouteTimer.Contracts.Training;

namespace RouteTimer.Client.Api;

public sealed class RouteTimerApiClient(HttpClient httpClient) : IRouteTimerApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<ProfileResponse?> GetProfileAsync(CancellationToken ct) =>
        GetOptionalAsync<ProfileResponse>("/api/profile", ct);

    public Task<ProfileResponse> UpdateProfileAsync(UpdateProfileRequest request, CancellationToken ct) =>
        SendJsonAsync<ProfileResponse>(HttpMethod.Put, "/api/profile", request, ct);

    public Task<IReadOnlyList<TrainingActivitySummaryResponse>> GetTrainingActivitiesAsync(CancellationToken ct) =>
        GetRequiredAsync<IReadOnlyList<TrainingActivitySummaryResponse>>("/api/training-activities", ct);

    public Task<TrainingActivityDetailResponse?> GetTrainingActivityAsync(Guid id, CancellationToken ct) =>
        GetOptionalAsync<TrainingActivityDetailResponse>($"/api/training-activities/{id}", ct);

    public async Task<TrainingUploadBatchResponse> UploadTrainingActivitiesAsync(IReadOnlyList<ClientFileUpload> files, CancellationToken ct)
    {
        using var content = CreateMultipartContent(files, "files");
        return await SendAsync<TrainingUploadBatchResponse>(HttpMethod.Post, "/api/training-activities", content, ct);
    }

    public Task<bool> DeleteTrainingActivityAsync(Guid id, CancellationToken ct) =>
        DeleteAsync($"/api/training-activities/{id}", ct);

    public Task<ModelStatusResponse> GetModelStatusAsync(CancellationToken ct) =>
        GetRequiredAsync<ModelStatusResponse>("/api/models/current", ct);

    public Task<ModelRebuildResponse> RebuildModelAsync(CancellationToken ct) =>
        SendAsync<ModelRebuildResponse>(HttpMethod.Post, "/api/models/rebuild", content: null, ct);

    public Task<IReadOnlyList<PredictionSummaryResponse>> GetPredictionsAsync(CancellationToken ct) =>
        GetRequiredAsync<IReadOnlyList<PredictionSummaryResponse>>("/api/predictions", ct);

    public async Task<PredictionSubmissionResponse> SubmitPredictionAsync(ClientFileUpload file, CancellationToken ct)
    {
        using var content = CreateMultipartContent([file], "file");
        return await SendAsync<PredictionSubmissionResponse>(HttpMethod.Post, "/api/predictions", content, ct);
    }

    public Task<PredictionDetailResponse?> GetPredictionAsync(Guid id, CancellationToken ct) =>
        GetOptionalAsync<PredictionDetailResponse>($"/api/predictions/{id}", ct);

    public Task<bool> DeletePredictionAsync(Guid id, CancellationToken ct) =>
        DeleteAsync($"/api/predictions/{id}", ct);

    public Task<JobResponse?> GetJobAsync(Guid id, CancellationToken ct) =>
        GetOptionalAsync<JobResponse>($"/api/jobs/{id}", ct);

    public async Task LocalLogoutAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private async Task<T> GetRequiredAsync<T>(string path, CancellationToken ct) =>
        await SendAsync<T>(HttpMethod.Get, path, content: null, ct);

    private async Task<T?> GetOptionalAsync<T>(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }

        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredJsonAsync<T>(response, ct);
    }

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string path, object payload, CancellationToken ct)
    {
        using var content = JsonContent.Create(payload, options: JsonOptions);
        return await SendAsync<T>(method, path, content, ct);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);
        return await ReadRequiredJsonAsync<T>(response, ct);
    }

    private async Task<bool> DeleteAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        await EnsureSuccessAsync(response, ct);
        return true;
    }

    private static async Task<T> ReadRequiredJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        return value ?? throw new InvalidOperationException("The API response body was empty.");
    }

    private static MultipartFormDataContent CreateMultipartContent(IReadOnlyList<ClientFileUpload> files, string fieldName)
    {
        var content = new MultipartFormDataContent();
        foreach (var file in files)
        {
            var stream = file.OpenReadStream();
            content.Add(new StreamContent(stream), fieldName, file.FileName);
        }

        return content;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw await CreateProblemAsync(response, ct);
    }

    private static async Task<ApiProblemException> CreateProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        const int maxFieldLength = 512;
        const string fallbackCode = "request-failed";
        const string fallbackTitle = "Request failed";
        const string fallbackDetail = "The server returned an error response.";

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ApiProblemException(response.StatusCode, fallbackCode, fallbackTitle, fallbackDetail);
            }

            var root = document.RootElement;
            var code = GetString(root, "code", maxFieldLength) ?? fallbackCode;
            var title = GetString(root, "title", maxFieldLength) ?? fallbackTitle;
            var detail = GetString(root, "detail", maxFieldLength) ?? fallbackDetail;
            var errors = GetErrors(root, maxFieldLength);
            return new ApiProblemException(response.StatusCode, code, title, detail, errors);
        }
        catch (JsonException)
        {
            return new ApiProblemException(response.StatusCode, fallbackCode, fallbackTitle, fallbackDetail);
        }
        catch (NotSupportedException)
        {
            return new ApiProblemException(response.StatusCode, fallbackCode, fallbackTitle, fallbackDetail);
        }
    }

    private static Dictionary<string, string[]> GetErrors(JsonElement root, int maxFieldLength)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!root.TryGetProperty("errors", out var errorsElement) || errorsElement.ValueKind != JsonValueKind.Object)
        {
            return errors;
        }

        foreach (var property in errorsElement.EnumerateObject())
        {
            var values = property.Value.ValueKind switch
            {
                JsonValueKind.Array => property.Value
                    .EnumerateArray()
                    .Where(element => element.ValueKind == JsonValueKind.String)
                    .Select(element => Limit(element.GetString(), maxFieldLength))
                    .Where(value => value is not null)
                    .Cast<string>()
                    .ToArray(),
                JsonValueKind.String => [Limit(property.Value.GetString(), maxFieldLength)!],
                _ => []
            };

            if (values.Length > 0)
            {
                errors[property.Name] = values;
            }
        }

        return errors;
    }

    private static string? GetString(JsonElement root, string propertyName, int maxFieldLength)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return Limit(value.GetString(), maxFieldLength);
    }

    private static string? Limit(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}

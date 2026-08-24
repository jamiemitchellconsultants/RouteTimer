using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using RouteTimer.Contracts.Profile;
using RouteTimer.Contracts.Training;
using RouteTimer.Contracts.Predictions;
using RouteTimer.Contracts.Jobs;
using RouteTimer.Contracts.Errors;
using RouteTimer.Api;
using RouteTimer.Api.Auth;
using RouteTimer.Api.Workers;
using RouteTimer.Persistence;
using RouteTimer.Persistence.Repositories;
using RouteTimer.Persistence.Jobs;
using RouteTimer.Services.Activities;
using RouteTimer.Services.Profile;
using RouteTimer.Services.Models;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Predictions;
using RouteTimer.Services.Training;
using RouteTimer.Services.Jobs;
using RouteTimer.Services.Routes;
using RouteTimer.Services.Validation;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks()
    .AddDbContextCheck<RouteTimerDbContext>("database", tags: ["ready"]);
var connectionString = builder.Configuration.GetConnectionString("RouteTimer")
    ?? "Host=localhost;Database=routetimer;Username=routetimer;Password=routetimer";
builder.Services.AddDbContext<RouteTimerDbContext>(options => options.UseNpgsql(connectionString));
if (builder.Configuration.GetValue("Database:ApplyMigrations", false))
{
    builder.Services.AddHostedService<DatabaseMigrationService>();
}
builder.Services.AddScoped<IStoredUploadRepository, StoredUploadRepository>();
builder.Services.AddScoped<ITrainingActivityRepository, TrainingActivityRepository>();
builder.Services.AddScoped<IRiderModelRepository, RiderModelRepository>();
builder.Services.AddScoped<IJobQueue, PostgresJobQueue>();
builder.Services.AddScoped<IProfileRepository, ProfileRepository>();
builder.Services.AddScoped<IPredictionRepository, PredictionRepository>();
builder.Services.AddScoped<IJobRepository, JobRepository>();
builder.Services.AddScoped<TrainingUploadService>();
builder.Services.AddScoped<ProfileService>();
builder.Services.AddScoped<PredictionSubmissionService>();
builder.Services.AddScoped<PredictionQueryService>();
builder.Services.AddSingleton<IGpxRouteParser, GpxRouteParser>();
builder.Services.AddSingleton<IRouteProcessor>(_ => new RouteProcessor(RouteProcessingOptions.Default));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IFitActivityParser, FitActivityParser>();
builder.Services.AddSingleton<ITrainingCleaner>(_ => new TrainingCleaner(RouteProcessingOptions.Default));
builder.Services.AddSingleton<IPowerModelBuilder, PowerModelBuilder>();
builder.Services.AddSingleton<IRoutePredictor, RoutePredictor>();
builder.Services.AddSingleton<IModelValidator, ModelValidator>();
builder.Services.AddScoped<IJobHandler, ParseTrainingJobHandler>();
builder.Services.AddScoped<IJobHandler, BuildModelJobHandler>();
builder.Services.AddScoped<IJobHandler, PredictionJobHandler>();
builder.Services.AddHostedService<AnalysisWorker>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Keycloak:Authority"];
        options.Audience = "routetimer-api";
        options.RequireHttpsMetadata = true;
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                KeycloakRealmRoleMapper.AddRealmRoles(context.Principal);
                return Task.CompletedTask;
            }
        };
    });
var riderPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireRole("rider")
        .Build();
builder.Services.AddAuthorizationBuilder().SetDefaultPolicy(riderPolicy).SetFallbackPolicy(riderPolicy);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 55L * 1024 * 1024);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 55L * 1024 * 1024);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = static _ => false,
    ResponseWriter = static (context, _) => context.Response.WriteAsync("Healthy")
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = static registration => registration.Tags.Contains("ready")
}).AllowAnonymous();
app.MapGet("/api/profile", async (ProfileService profiles, CancellationToken cancellationToken) =>
    (await profiles.GetAsync(cancellationToken)) is { } profile
        ? Results.Ok(new ProfileResponse(profile.RiderWeightKg, profile.BikeAndEquipmentWeightKg))
        : Results.NotFound()).RequireAuthorization();
app.MapPut("/api/profile", async (UpdateProfileRequest request, ProfileService profiles, CancellationToken cancellationToken) =>
    {
        try
        {
            var profile = await profiles.UpdateAsync(request.RiderWeightKg, request.BikeAndEquipmentWeightKg, cancellationToken);
            return Results.Ok(new ProfileResponse(profile.RiderWeightKg, profile.BikeAndEquipmentWeightKg));
        }
        catch (ProfileValidationException exception)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["profile"] = [exception.Message] });
        }
    })
    .RequireAuthorization();
app.MapPost("/api/training/uploads", async (HttpRequest request, TrainingUploadService uploads, CancellationToken cancellationToken) =>
    {
        if (!request.HasFormContentType)
        {
            return Results.BadRequest(new { code = "multipart-required" });
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var batch = new List<TrainingUpload>(form.Files.Count);
        foreach (var file in form.Files)
        {
            await using var content = new MemoryStream();
            await file.CopyToAsync(content, cancellationToken);
            batch.Add(new TrainingUpload(file.FileName, content.ToArray()));
        }

        var results = await uploads.AcceptAsync(batch, cancellationToken);
        return Results.Ok(results.Select(result => new TrainingUploadResponse(result.FileName, result.Outcome.ToString().ToLowerInvariant(), result.ErrorCode)));
    })
    .RequireAuthorization();
app.MapPost("/api/predictions", async (HttpRequest request, PredictionSubmissionService submissions, CancellationToken cancellationToken) =>
    {
        if (!request.HasFormContentType || !request.ContentType!.Contains("boundary=", StringComparison.OrdinalIgnoreCase))
        {
            return Problem(StatusCodes.Status400BadRequest, ErrorCodes.MultipartRequired, "A multipart GPX upload is required.");
        }

        try
        {
            var files = (await request.ReadFormAsync(cancellationToken)).Files;
            if (files.Count != 1 || !files[0].FileName.EndsWith(".gpx", StringComparison.OrdinalIgnoreCase))
                return Problem(StatusCodes.Status400BadRequest, ErrorCodes.PredictionGpxRequired, "A single .gpx route upload is required.");
            var file = files[0];
            if (file.Length > 50L * 1024 * 1024)
                return Problem(StatusCodes.Status413PayloadTooLarge, ErrorCodes.GpxTooLarge, "The GPX upload exceeds 50 MB.");
            await using var input = file.OpenReadStream();
            var accepted = await submissions.SubmitAsync(new PredictionUpload(file.FileName, input), cancellationToken);
            return Results.Accepted($"/api/predictions/{accepted.PredictionId}", new PredictionSubmissionResponse(accepted.PredictionId, accepted.JobId, accepted.ModelId));
        }
        catch (PredictionSubmissionException exception)
        {
            var status = exception.Code is ErrorCodes.ProfileRequired or ErrorCodes.ModelNotReady
                ? StatusCodes.Status409Conflict
                : exception.Code == ErrorCodes.GpxTooLarge ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest;
            return Problem(status, exception.Code, exception.Message);
        }
        catch (BadHttpRequestException)
        {
            return Problem(StatusCodes.Status400BadRequest, ErrorCodes.MultipartRequired, "The multipart request is malformed.");
        }
        catch (InvalidDataException)
        {
            return Problem(StatusCodes.Status400BadRequest, ErrorCodes.MultipartRequired, "The multipart request is malformed.");
        }
        catch (InvalidOperationException)
        {
            return Problem(StatusCodes.Status400BadRequest, ErrorCodes.MultipartRequired, "The multipart request is malformed.");
        }
        catch (IOException exception) when (!exception.Message.Contains("limit", StringComparison.OrdinalIgnoreCase))
        {
            return Problem(StatusCodes.Status400BadRequest, ErrorCodes.MultipartRequired, "The multipart request is malformed.");
        }
        catch (IOException)
        {
            return Problem(StatusCodes.Status413PayloadTooLarge, ErrorCodes.GpxTooLarge, "The GPX upload exceeds 50 MB.");
        }
    })
    .RequireAuthorization();
app.MapGet("/api/predictions", async (PredictionQueryService predictions, CancellationToken cancellationToken) =>
    Results.Ok((await predictions.GetSummariesAsync(cancellationToken)).Select(ToSummary))).RequireAuthorization();
app.MapGet("/api/predictions/{id:guid}", async (Guid id, PredictionQueryService predictions, CancellationToken cancellationToken) =>
    (await predictions.GetAsync(id, cancellationToken)) is { } prediction
        ? Results.Ok(new PredictionDetailResponse(ToDetailSummary(prediction), prediction.Segments.Select(ToSegment).ToList()))
        : Results.NotFound()).RequireAuthorization();
app.MapGet("/api/jobs/{id:guid}", async (Guid id, IJobRepository jobs, CancellationToken cancellationToken) =>
    (await jobs.GetAsync(id, cancellationToken)) is { } job
        ? Results.Ok(new JobResponse(job.Id, job.Type.ToString(), job.SubjectId, job.State.ToString(), job.AttemptCount, job.CreatedAt,
            job.State == RouteTimer.Domain.Jobs.JobState.Running ? job.LeaseExpiresAt : null, job.DiagnosticCode, job.DiagnosticMessage))
        : Results.NotFound()).RequireAuthorization();

app.Run();

static IResult Problem(int status, string code, string detail) => Results.Problem(statusCode: status, detail: detail,
    extensions: new Dictionary<string, object?> { ["code"] = code });

static PredictionSummaryResponse ToSummary(RouteTimer.Services.Persistence.PredictionSummary prediction) => new(
    prediction.Id, prediction.State.ToString(), prediction.DistanceMetres, prediction.AscentMetres, prediction.MovingTime?.TotalSeconds,
    prediction.AverageSpeedMetresPerSecond, prediction.AveragePowerWatts, prediction.Confidence?.ToString(), prediction.Warnings,
    prediction.ModelId, prediction.ModelVersion, prediction.ModelWasCalibrated, prediction.Validation.Status.ToString(), prediction.Validation.MedianAbsolutePercentageError,
    prediction.Validation.P90AbsolutePercentageError, prediction.Profile.RiderWeightKg, prediction.Profile.BikeAndEquipmentWeightKg,
    prediction.Assumptions.Surface, prediction.Assumptions.Wind, prediction.Assumptions.Weather, prediction.Assumptions.MovingOnly,
    prediction.CreatedAt, prediction.CompletedAt);

static PredictionSummaryResponse ToDetailSummary(RouteTimer.Services.Persistence.PredictionDetail prediction) => new(
    prediction.Id, prediction.State.ToString(), prediction.DistanceMetres, prediction.AscentMetres, prediction.MovingTime?.TotalSeconds,
    prediction.AverageSpeedMetresPerSecond, prediction.AveragePowerWatts, prediction.Confidence?.ToString(), prediction.Warnings,
    prediction.ModelId, prediction.ModelVersion, prediction.ModelWasCalibrated, prediction.Validation.Status.ToString(), prediction.Validation.MedianAbsolutePercentageError,
    prediction.Validation.P90AbsolutePercentageError, prediction.Profile.RiderWeightKg, prediction.Profile.BikeAndEquipmentWeightKg,
    prediction.Assumptions.Surface, prediction.Assumptions.Wind, prediction.Assumptions.Weather, prediction.Assumptions.MovingOnly,
    prediction.CreatedAt, prediction.CompletedAt);

static PredictionSegmentResponse ToSegment(RouteTimer.Services.Persistence.PersistedPredictionSegment segment) => new(
    segment.Sequence, segment.Latitude, segment.Longitude, segment.ElevationMetres, segment.CumulativeDistanceMetres,
    segment.SegmentDistanceMetres, segment.Gradient, segment.CurvaturePerMetre, segment.PredictedPowerWatts,
    segment.PredictedSpeedMetresPerSecond, segment.SegmentMovingTime.TotalSeconds, segment.CumulativeMovingTime.TotalSeconds, segment.Confidence.ToString());

public partial class Program;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using RouteTimer.Contracts.Profile;
using RouteTimer.Contracts.Training;
using RouteTimer.Contracts.Predictions;
using RouteTimer.Api;
using RouteTimer.Persistence;
using RouteTimer.Persistence.Repositories;
using RouteTimer.Services.Profile;
using RouteTimer.Services.Persistence;
using RouteTimer.Services.Training;
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
builder.Services.AddScoped<IProfileRepository, ProfileRepository>();
builder.Services.AddScoped<TrainingUploadService>();
builder.Services.AddScoped<ProfileService>();
builder.Services.AddSingleton<IGpxRouteParser, GpxRouteParser>();
builder.Services.AddSingleton<IRouteProcessor>(_ => new RouteProcessor(RouteProcessingOptions.Default));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Keycloak:Authority"];
        options.Audience = "routetimer-api";
        options.RequireHttpsMetadata = true;
    });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireRole("rider")
        .Build());

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
app.MapPost("/api/predictions", async (HttpRequest request, IGpxRouteParser parser, IRouteProcessor processor, CancellationToken cancellationToken) =>
    {
        if (!request.HasFormContentType)
        {
            return Results.BadRequest(new { code = "multipart-required" });
        }

        var file = (await request.ReadFormAsync(cancellationToken)).Files.SingleOrDefault();
        if (file is null || !file.FileName.EndsWith(".gpx", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { code = "prediction-gpx-required" });
        }

        try
        {
            await using var input = file.OpenReadStream();
            var parsed = await parser.ParseAsync(input, cancellationToken);
            var route = processor.Process(parsed.Points);
            return Results.Ok(new PredictionRoutePreview(parsed.Name, route.DistanceMetres, route.AscentMetres, route.Samples.Count));
        }
        catch (RouteInputException exception)
        {
            return Results.BadRequest(new { code = "invalid-gpx", message = exception.Message });
        }
    })
    .RequireAuthorization();

app.Run();

public partial class Program;

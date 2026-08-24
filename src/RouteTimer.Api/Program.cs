using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using RouteTimer.Contracts.Profile;
using RouteTimer.Contracts.Training;
using RouteTimer.Services.Profile;
using RouteTimer.Services.Training;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
builder.Services.AddSingleton<ProfileService>();
builder.Services.AddSingleton<TrainingUploadService>();
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

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    ResponseWriter = static (context, _) => context.Response.WriteAsync("Healthy")
}).AllowAnonymous();
app.MapGet("/api/profile", (ProfileService profiles) => profiles.Current is { } profile
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

app.Run();

public partial class Program;

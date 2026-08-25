using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using RouteTimer.Api;
using RouteTimer.Api.Auth;
using RouteTimer.Api.Endpoints;
using RouteTimer.Api.Security;
using RouteTimer.Api.Workers;
using RouteTimer.Contracts.Uploads;
using RouteTimer.Persistence;
using RouteTimer.Persistence.Repositories;
using RouteTimer.Persistence.Jobs;
using RouteTimer.Services.Activities;
using RouteTimer.Services.Profile;
using RouteTimer.Services.Models;
using RouteTimer.Services.Physics;
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
builder.Services.AddScoped<ITrainingUploadRepository, TrainingUploadRepository>();
builder.Services.AddScoped<ITrainingActivityRepository, TrainingActivityRepository>();
builder.Services.AddScoped<IRiderModelRepository, RiderModelRepository>();
builder.Services.AddScoped<IJobQueue, PostgresJobQueue>();
builder.Services.AddScoped<IJobProgressReporter, JobProgressReporter>();
builder.Services.AddScoped<IProfileRepository, ProfileRepository>();
builder.Services.AddScoped<ILocalCredentialRepository, LocalCredentialRepository>();
builder.Services.AddScoped<LocalCredentialService>();
builder.Services.AddScoped<IPredictionRepository, PredictionRepository>();
builder.Services.AddScoped<IJobRepository, JobRepository>();
builder.Services.AddScoped<TrainingUploadService>();
builder.Services.AddScoped<TrainingActivityQueryService>();
builder.Services.AddScoped<TrainingActivityDeletionService>();
builder.Services.AddScoped<ProfileService>();
builder.Services.AddScoped<ModelStatusService>();
builder.Services.AddScoped<ModelRebuildService>();
builder.Services.AddScoped<PredictionSubmissionService>();
builder.Services.AddScoped<PredictionQueryService>();
builder.Services.AddScoped<PredictionDeletionService>();
builder.Services.AddSingleton<IGpxRouteParser, GpxRouteParser>();
builder.Services.AddSingleton<IRouteProcessor>(_ => new RouteProcessor(RouteProcessingOptions.Default));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IFitActivityParser, FitActivityParser>();
builder.Services.AddSingleton<ITrainingCleaner>(_ => new TrainingCleaner(RouteProcessingOptions.Default));
builder.Services.AddSingleton<ITrainingGeometryEnricher>(_ => new TrainingGeometryEnricher(RouteProcessingOptions.Default));
builder.Services.AddSingleton<IPowerModelBuilder, PowerModelBuilder>();
builder.Services.AddSingleton<IPhysicsCalibrator, PhysicsCalibrator>();
builder.Services.AddSingleton<IDescentLimitBuilder, DescentLimitBuilder>();
builder.Services.AddSingleton<IDescentSpeedLimiter, DescentSpeedLimiter>();
builder.Services.AddSingleton<IRoutePredictor, RoutePredictor>();
builder.Services.AddSingleton<IModelValidator, ModelValidator>();
builder.Services.AddScoped<IJobHandler, ParseTrainingJobHandler>();
builder.Services.AddScoped<IJobHandler, BuildModelJobHandler>();
builder.Services.AddScoped<IJobHandler, PredictionJobHandler>();
builder.Services.AddHostedService<AnalysisWorker>();
var authMode = AuthModeResolver.Resolve(builder.Configuration);
if (authMode == AuthMode.Keycloak)
{
    // Without an authority the bearer handler builds no configuration manager, so the deployment
    // starts, reports healthy, and then silently rejects every token. Refuse to start instead,
    // for the same reason Auth:Mode itself has no default.
    var authority = builder.Configuration["Keycloak:Authority"];
    if (string.IsNullOrWhiteSpace(authority))
    {
        throw new InvalidOperationException(
            "Keycloak:Authority must be set when Auth:Mode is 'Keycloak'. It is the realm's issuer " +
            "URL, for example https://auth.example.com/realms/routetimer, and both token validation " +
            "and the client's sign-in redirect depend on it. Without it the application would accept " +
            "no request at all.");
    }

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = authority;
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
}
else
{
    builder.Services.AddAuthentication(LocalAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(LocalAuthenticationDefaults.AuthenticationScheme, options =>
        {
            options.Cookie.Name = LocalAuthenticationDefaults.CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            // Local mode is expected to run over plain HTTP on loopback, where an
            // unconditionally Secure cookie would never be sent. SameAsRequest marks it
            // Secure whenever the request itself arrived over HTTPS.
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.SlidingExpiration = true;
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            // This is an API, not a server-rendered site: answer with status codes rather
            // than redirecting to a login page that does not exist on the server.
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
            // The cookie is a self-contained, data-protected ticket with a 30-day sliding
            // expiry and no server-side session store, so nothing else re-checks the credential
            // after sign-in. Without this, deleting the credential row -- the recovery path the
            // setup-conflict response itself recommends -- would lock the rider out of setup
            // while leaving any session already issued fully valid for up to 30 more days.
            // Re-validating on every request closes that: once the row is gone, the very next
            // request the existing cookie is used on gets signed out instead of let through.
            options.Events.OnValidatePrincipal = async context =>
            {
                var credentials = context.HttpContext.RequestServices.GetRequiredService<LocalCredentialService>();
                if (await credentials.IsSetupRequiredAsync(context.HttpContext.RequestAborted))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(LocalAuthenticationDefaults.AuthenticationScheme);
                }
            };
        });
}
var riderPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireRole(LocalAuthenticationDefaults.RiderRole)
        .Build();
builder.Services.AddAuthorizationBuilder().SetDefaultPolicy(riderPolicy).SetFallbackPolicy(riderPolicy);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = UploadLimits.MaximumTrainingRequestBytes);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = UploadLimits.MaximumTrainingRequestBytes);

var app = builder.Build();

app.UseSameOriginEnforcement();
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
app.MapProfileEndpoints();
app.MapTrainingEndpoints();
app.MapModelsEndpoints();
app.MapPredictionEndpoints();
app.MapJobEndpoints();
app.MapAuthEndpoints(authMode);

app.Run();

public partial class Program;

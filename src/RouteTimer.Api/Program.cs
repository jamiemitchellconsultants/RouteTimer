using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using RouteTimer.Api;
using RouteTimer.Api.Auth;
using RouteTimer.Api.Health;
using RouteTimer.Contracts.Errors;
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

// Read once and shared: MigrationState's "is a migration required at all" answer and the decision
// to register the migration service must never be able to diverge, or readiness could report
// healthy while a schema migration nobody is running to complete ever runs.
var applyMigrations = builder.Configuration.GetValue("Database:ApplyMigrations", false);
builder.Services.AddSingleton(new MigrationState(applyMigrations));
builder.Services.AddHealthChecks()
    .AddDbContextCheck<RouteTimerDbContext>("database", tags: ["ready"])
    .AddCheck<MigrationsReadyHealthCheck>("migrations", tags: ["ready"]);
var connectionString = builder.Configuration.GetConnectionString("RouteTimer")
    ?? "Host=localhost;Database=routetimer;Username=routetimer;Password=routetimer";
builder.Services.AddDbContext<RouteTimerDbContext>(options => options.UseNpgsql(connectionString));
if (applyMigrations)
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
builder.Services.AddSingleton(sp => new CredentialRevalidationCache(
    sp.GetRequiredService<TimeProvider>(),
    TimeSpan.FromSeconds(CredentialRevalidationCache.DefaultTtlSeconds)));
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
            // The check is routed through CredentialRevalidationCache rather than calling
            // LocalCredentialService directly: this handler runs for every cookie-bearing
            // request -- API calls, static files, health checks -- and a Blazor WASM boot alone
            // fetches 100+ files, each of which would otherwise be its own database read for a
            // row that essentially never changes.
            options.Events.OnValidatePrincipal = async context =>
            {
                var cache = context.HttpContext.RequestServices.GetRequiredService<CredentialRevalidationCache>();
                var credentials = context.HttpContext.RequestServices.GetRequiredService<LocalCredentialService>();
                if (await cache.IsSetupRequiredAsync(credentials, context.HttpContext.RequestAborted))
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

builder.Services.AddSingleton<LoginAttemptTracker>();
builder.Services.AddRateLimiter(options =>
{
    // OnRejected owns the status: it writes the response itself, so setting RejectionStatusCode
    // here as well would be dead configuration that a future reader would expect to be load-bearing.
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        var detail = "Too many requests. Wait before trying again.";
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            var seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
            context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            detail = $"Too many requests. Wait {seconds} seconds before trying again.";
        }

        await Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                detail: detail,
                extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.RequestRateExceeded })
            .ExecuteAsync(context.HttpContext);
    };

    // Both policies limit Local mode only, and deliberately with a single global bucket: one rider,
    // one machine, so a global limit is the intended semantics rather than an accident.
    //
    // Keycloak mode gets no limiter here at all. It is the shared public deployment behind the
    // Caddy ingress, where every request arrives from the proxy's own address -- so any partition
    // this process can compute is either that one shared address (a global bucket, letting one
    // browser's page loads lock out every other user) or an X-Forwarded-For value the caller
    // chooses. Neither is rate limiting. The ingress is the only layer that knows who the client
    // is, so per-client limiting for that deployment belongs there.
    options.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, _ =>
        authMode == AuthMode.Local
            ? RateLimitPartition.GetFixedWindowLimiter("auth-login", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            })
            : RateLimitPartition.GetNoLimiter<string>("ingress-owns-this"));

    options.AddPolicy(AuthEndpoints.AuthRateLimitPolicy, _ =>
        authMode == AuthMode.Local
            ? RateLimitPartition.GetFixedWindowLimiter("auth-general", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            })
            : RateLimitPartition.GetNoLimiter<string>("ingress-owns-this"));
});

var app = builder.Build();

// Despite being registered first, this does NOT run before authentication/authorization: when
// UseAuthentication/UseAuthorization are not called explicitly, WebApplication auto-inserts them
// ahead of any user middleware regardless of source order, so the actual sequence is routing ->
// authentication -> authorization -> this middleware -> endpoint. A cross-site request carrying no
// (or an invalid) cookie is therefore rejected 401 by authorization before ever reaching here. That
// is not a security gap: the load-bearing case -- a cross-site request that DOES carry a valid
// cookie, which is what SameSite=Strict alone fails to stop -- passes authentication/authorization
// (the cookie is genuine) and is still rejected 403 here, before the endpoint runs.
app.UseSameOriginEnforcement();
app.UseRateLimiter();
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

// Every unmapped GET -- every client-side route, and the OIDC redirect and post-logout callbacks
// Keycloak mode sends the browser to -- must still serve the compiled WASM app rather than 404 or,
// worse, 401 from the fallback authorization policy applying to an endpointless request. Anonymous:
// the app itself decides what to render once it boots, including redirecting an unauthenticated
// rider to sign in, and that decision cannot be made if the server blocks the page from loading.
//
// MapFallbackToFile matches every HTTP method on any unmapped path, not only GET, and answers a
// non-GET request with 405 rather than serving the file. That would turn a POST to a typo'd or
// legacy API path into "405 Method Not Allowed" instead of the "404 Not Found" several tests
// pin as the documented contract for a route that does not exist. Route explicitly to GET/HEAD
// only, so every other method continues to fall through to ordinary unmatched-route 404.
app.MapFallback("{**path}", (HttpContext context) =>
{
    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
    {
        return Results.NotFound();
    }

    var indexFile = context.RequestServices.GetRequiredService<IWebHostEnvironment>()
        .WebRootFileProvider.GetFileInfo("index.html");
    return indexFile.Exists
        ? Results.File(indexFile.CreateReadStream(), "text/html")
        : Results.NotFound();
}).AllowAnonymous();

app.Run();

public partial class Program;

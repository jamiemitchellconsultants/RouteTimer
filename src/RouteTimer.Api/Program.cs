using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using RouteTimer.Api;
using RouteTimer.Api.Auth;
using RouteTimer.Api.Health;
using RouteTimer.Api.Routing;
using RouteTimer.Contracts.Errors;
using RouteTimer.Api.Endpoints;
using RouteTimer.Api.Garmin;
using RouteTimer.Api.Security;
using RouteTimer.Api.Workers;
using RouteTimer.Contracts.Uploads;
using RouteTimer.Persistence;
using RouteTimer.Persistence.Repositories;
using RouteTimer.Persistence.Jobs;
using RouteTimer.Services.Activities;
using RouteTimer.Services.Garmin;
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
builder.Services.AddScoped<IGarminConnectionRepository, GarminConnectionRepository>();
builder.Services.AddScoped<IGarminActivityImportRepository, GarminActivityImportRepository>();
builder.Services.AddScoped<TrainingUploadService>();
builder.Services.AddScoped<TrainingActivityQueryService>();
builder.Services.AddScoped<TrainingActivityDeletionService>();
builder.Services.AddScoped<ProfileService>();
builder.Services.AddScoped<ModelStatusService>();
builder.Services.AddScoped<ModelRebuildService>();
builder.Services.AddScoped<PredictionSubmissionService>();
builder.Services.AddScoped<PredictionQueryService>();
builder.Services.AddScoped<PredictionDeletionService>();
builder.Services.AddScoped<GarminConnectionService>();
builder.Services.AddScoped<GarminActivityService>();
var encodedGarminKey = builder.Configuration["Garmin:TokenEncryptionKey"]
    ?? throw new InvalidOperationException("Garmin:TokenEncryptionKey is required.");
byte[] garminKey;
try
{
    garminKey = Convert.FromBase64String(encodedGarminKey);
}
catch (FormatException exception)
{
    throw new InvalidOperationException("Garmin:TokenEncryptionKey must be base64.", exception);
}

AesGcmGarminTokenProtector garminTokenProtector;
try
{
    if (garminKey.Length != 32)
    {
        throw new InvalidOperationException("Garmin:TokenEncryptionKey must decode to exactly 32 bytes.");
    }

    garminTokenProtector = new AesGcmGarminTokenProtector(garminKey);
}
finally
{
    CryptographicOperations.ZeroMemory(garminKey);
}

builder.Services.AddSingleton<IGarminTokenProtector>(_ => garminTokenProtector);
builder.Services.AddSingleton<GarminOperationGate>();
builder.Services.AddHttpClient<IGarminAdapterClient, GarminAdapterClient>(client =>
{
    var baseUrl = builder.Configuration["GarminAdapter:BaseUrl"]
        ?? throw new InvalidOperationException("GarminAdapter:BaseUrl is required.");
    client.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromMinutes(2);
})
.RedactLoggedHeaders(["X-RouteTimer-Garmin-Token"]);
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

// This runs before authentication/authorization regardless of endpoint state, since it never
// touches context.GetEndpoint() -- unlike static files below, whose serve-or-not decision does
// depend on whether routing has already selected an endpoint for the request. A cross-site
// request carrying no (or an invalid) cookie is rejected 401 by authorization once routing and
// auth run further down. That is not a security gap: the load-bearing case -- a cross-site
// request that DOES carry a valid cookie, which is what SameSite=Strict alone fails to stop --
// is rejected 403 right here, before authentication/authorization or the endpoint ever run.
app.UseSameOriginEnforcement();

// UseDefaultFiles/UseStaticFiles MUST run before routing selects an endpoint: StaticFileMiddleware
// checks context.GetEndpoint() and silently declines to serve a physical file once an endpoint is
// already assigned, to avoid double-handling a request. MapFallback below registers "{**path}",
// which matches every unmatched path including every static asset's own path -- so if routing ran
// first (as it would with no explicit UseRouting() call: WebApplication auto-inserts routing at
// the very start of the pipeline, before any of this file's app.Use()/app.Map() calls, regardless
// of their source order), every static asset request would already be endpoint-bound to the SPA
// fallback by the time static files middleware got a turn, and it would serve index.html's HTML
// for every JS/CSS/JSON/WASM request instead of the real file. Verified directly: the WebAssembly
// bundle, appsettings.json, and even favicon.png all came back as index.html's HTML, byte for
// byte, until this explicit UseRouting() call was added in exactly this position.
app.UseDefaultFiles();

// The default FileExtensionContentTypeProvider has no mapping for .dat -- the ICU internationalization
// data files (icudt_*.dat) the WebAssembly runtime downloads at boot. ServeUnknownFileTypes defaults
// to false, so without this, StaticFileMiddleware silently declines to serve them (the same "falls
// through to the SPA fallback" failure the UseRouting() reordering above fixed for every other
// extension, but this one is a genuinely separate gap: reordering alone does not teach the default
// provider a new extension). Verified against the actual published wwwroot: .dat is the only
// extension present there without a built-in mapping.
var staticFileTypeProvider = new FileExtensionContentTypeProvider();
staticFileTypeProvider.Mappings[".dat"] = "application/octet-stream";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = staticFileTypeProvider });

app.UseRouting();

// Explicit, not relying on WebApplication's automatic insertion: with UseRouting() called
// explicitly (required above, see its comment), automatic insertion of authentication/
// authorization no longer lands where a plain reading of the docs suggests. Verified directly --
// omitting these two lines and leaving insertion implicit made every AllowAnonymous auth endpoint
// (login, setup, logout, session, config) return 401, meaning AllowAnonymous's metadata wasn't
// being honored by whatever position auth ended up running at. Explicit calls here, immediately
// after routing and before anything that depends on the authenticated principal or the matched
// endpoint's authorization metadata, is the only arrangement confirmed to work correctly by the
// full Api test suite.
app.UseAuthentication();
app.UseAuthorization();

// UseRateLimiter MUST run after routing and authorization: LoginRateLimitPolicy/AuthRateLimitPolicy
// below are named per-endpoint policies applied via RequireRateLimiting on specific auth
// endpoints, and this middleware resolves which policy applies from the already-matched endpoint's
// metadata -- placing it before routing would make every policy lookup fail to find an endpoint
// and silently apply no limit at all. Running after authorization is safe specifically because
// every endpoint carrying one of these policies is also AllowAnonymous, so authorization never
// gates the requests this is meant to limit.
app.UseRateLimiter();

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
app.MapGarminEndpoints();
app.MapAuthEndpoints(authMode);

// Every unmapped GET -- every client-side route, and the OIDC redirect and post-logout callbacks
// Keycloak mode sends the browser to -- must still serve the compiled WASM app rather than 404 or,
// worse, 401 from the fallback authorization policy applying to an endpointless request. Anonymous:
// the app itself decides what to render once it boots, including redirecting an unauthenticated
// rider to sign in, and that decision cannot be made if the server blocks the page from loading.
// See SpaFallbackEndpoint for the method/prefix rules and why each exists; it is a plain function
// so those rules are unit-tested directly rather than only through a full HTTP round trip.
app.MapFallback("{**path}", (HttpContext context) => SpaFallbackEndpoint.Handle(
    context,
    context.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider))
    .AllowAnonymous();

app.Run();

public partial class Program;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Authentication.JwtBearer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();
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
app.MapGet("/api/profile", () => Results.Ok()).RequireAuthorization();

app.Run();

public partial class Program;

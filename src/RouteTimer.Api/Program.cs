using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    ResponseWriter = static (context, _) => context.Response.WriteAsync("Healthy")
}).AllowAnonymous();

app.Run();

public partial class Program;

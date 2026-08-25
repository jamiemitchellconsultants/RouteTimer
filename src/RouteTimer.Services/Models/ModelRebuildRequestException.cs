namespace RouteTimer.Services.Models;

public sealed class ModelRebuildRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

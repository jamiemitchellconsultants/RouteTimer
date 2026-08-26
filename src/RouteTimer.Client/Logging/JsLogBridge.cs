using Microsoft.JSInterop;

namespace RouteTimer.Client.Logging;

public sealed class JsLogBridge(ActionLog log)
{
    [JSInvokable]
    public void LogFromJs(string level, string message, string? detail)
    {
        switch (level)
        {
            case "Success": log.Success(message, detail); break;
            case "Warn": log.Warn(message, detail); break;
            case "Error": log.Error(message, detail); break;
            default: log.Info(message, detail); break;
        }
    }
}

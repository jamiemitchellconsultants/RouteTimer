namespace RouteTimer.Client.Api;

public sealed record ClientFileUpload(string FileName, long Size, Func<Stream> OpenReadStream);

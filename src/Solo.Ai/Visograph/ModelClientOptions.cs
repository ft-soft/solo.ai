using Solo.Toolbox.Configurations;

namespace Solo.Ai.Visograph;

[Configuration("VisographModelClient")]
public sealed class ModelClientOptions : ICustomConfiguration
{
    public bool Enabled { get; init; }
    public Uri? BaseUri { get; init; }
    public string ApiKey { get; init; } = string.Empty;
    public TimeSpan RequestTimeout { get; init; }
    public long MaxRequestBytes { get; init; }
    public long MaxResponseBytes { get; init; }

    public bool IsValid() => Enabled && BaseUri is { IsAbsoluteUri: true, Scheme: "http" or "https" } &&
        string.IsNullOrEmpty(BaseUri.UserInfo) && string.IsNullOrEmpty(BaseUri.Query) &&
        string.IsNullOrEmpty(BaseUri.Fragment) && BaseUri.AbsolutePath.EndsWith('/') &&
        !string.IsNullOrWhiteSpace(ApiKey) && RequestTimeout > TimeSpan.Zero && RequestTimeout <= TimeSpan.FromDays(1) &&
        MaxResponseBytes is > 0 and <= int.MaxValue && MaxRequestBytes is > 0 and <= int.MaxValue;
}

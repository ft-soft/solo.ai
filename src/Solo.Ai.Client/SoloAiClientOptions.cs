using Solo.Toolbox.Configurations;

namespace Solo.Ai.Client;

[Configuration("SoloAgent:Client")]
public sealed class SoloAiClientOptions : ICustomConfiguration
{
    public bool Enabled { get; init; }
    public Uri? BaseUri { get; init; }
    public TimeSpan RequestTimeout { get; init; }
    public TimeSpan TotalDeadline { get; init; }
    public long MaxRequestBytes { get; init; }
    public long MaxResponseBytes { get; init; }

    public bool IsValid() => Enabled && BaseUri is { IsAbsoluteUri: true, Scheme: "http" or "https" } &&
        string.IsNullOrEmpty(BaseUri.UserInfo) && string.IsNullOrEmpty(BaseUri.Query) &&
        string.IsNullOrEmpty(BaseUri.Fragment) && BaseUri.AbsolutePath.EndsWith('/') &&
        RequestTimeout > TimeSpan.Zero && RequestTimeout <= TotalDeadline &&
        TotalDeadline > TimeSpan.Zero && TotalDeadline <= TimeSpan.FromDays(1) &&
        MaxRequestBytes is > 0 and <= int.MaxValue && MaxResponseBytes is > 0 and <= int.MaxValue;
}

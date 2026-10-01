using Solo.Toolbox.Configurations;

namespace Solo.Ai.Api;

[Configuration("SoloAgentModelApi")]
public sealed class SoloAgentModelApiOptions : ICustomConfiguration
{
    public bool Enabled { get; init; }
    public long MaxRequestBytes { get; init; }
    public long MaxResponseBytes { get; init; }
    public TimeSpan RequestTimeout { get; init; }

    public bool IsValid() => Enabled && MaxRequestBytes is > 0 and <= int.MaxValue &&
        MaxResponseBytes is > 0 and <= int.MaxValue && RequestTimeout > TimeSpan.Zero && RequestTimeout <= TimeSpan.FromDays(1);
}

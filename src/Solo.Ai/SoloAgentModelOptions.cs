using Solo.Toolbox.Configurations;

namespace Solo.Ai;

[Configuration("SoloAgentModel")]
public sealed class SoloAgentModelOptions : ICustomConfiguration
{
    public bool Enabled { get; init; }
    public long MaxRequestBytes { get; init; }
    public TimeSpan TotalDeadline { get; init; }
    public TimeSpan ModelTimeout { get; init; }

    public bool IsValid() => Enabled && MaxRequestBytes is > 0 and <= int.MaxValue &&
        TotalDeadline > TimeSpan.Zero && TotalDeadline <= TimeSpan.FromDays(1) &&
        ModelTimeout > TimeSpan.Zero && ModelTimeout <= TotalDeadline;
}

using Solo.Toolbox.Configurations;

namespace Solo.Ai.Storage;

[Configuration("SoloAiStorage")]
public sealed class SqliteChatOptions : ICustomConfiguration
{
    public bool Enabled { get; init; }
    public string DatabasePath { get; init; } = "";
    public int BusyTimeoutSeconds { get; init; } = 5;
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(4);
    public int? ChatRetentionDays { get; init; }

    public bool IsValid() => Enabled && !string.IsNullOrWhiteSpace(DatabasePath) &&
        Path.IsPathFullyQualified(DatabasePath) && !DatabasePath.StartsWith(@"\\") &&
        !DatabasePath.StartsWith("//", StringComparison.Ordinal) &&
        BusyTimeoutSeconds is > 0 and <= 60 && RunTimeout > TimeSpan.Zero && RunTimeout <= TimeSpan.FromDays(1) &&
        (ChatRetentionDays is null or > 0);
}

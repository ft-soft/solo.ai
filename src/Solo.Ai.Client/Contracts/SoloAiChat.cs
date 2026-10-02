namespace Solo.Ai.Client;

public sealed record SoloAiChat(Guid ChatId, string Title, DateTimeOffset CreatedAt,
    DateTimeOffset? LastMessageAt, SoloAiRun? LatestRun);

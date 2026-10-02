namespace Solo.Ai.Client;

public sealed record SoloAiMessagePage(Guid ChatId, IReadOnlyList<SoloAiMessage> Messages, string? NextCursor);

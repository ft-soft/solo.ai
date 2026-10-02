namespace Solo.Ai.Client;

public sealed record SoloAiMessage(Guid MessageId, Guid ChatId, long Sequence, string Role,
    string Text, DateTimeOffset CreatedAt, SoloAiRun Run);

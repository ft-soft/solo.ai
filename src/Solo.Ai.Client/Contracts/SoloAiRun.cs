namespace Solo.Ai.Client;

public sealed record SoloAiRun(Guid RunId, Guid ChatId, Guid UserMessageId, Guid? RetryId,
    string State, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    DateTimeOffset DeadlineAt, string? OutcomeCode, Guid? AssistantMessageId);

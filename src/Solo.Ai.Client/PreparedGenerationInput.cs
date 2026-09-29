namespace Solo.Ai.Client;

// The caller must authenticate the sender, authorize chat/run ownership and read a fresh catalog.
// Correlation IDs and this DTO do not grant access to any chat or user.
public sealed record PreparedGenerationInput(
    Guid RequestId, Guid ChatId, Guid RunId, Guid MessageId, string Message,
    IReadOnlyList<ChatHistoryMessage> History, IReadOnlyList<DocumentCreateOption> Catalog);

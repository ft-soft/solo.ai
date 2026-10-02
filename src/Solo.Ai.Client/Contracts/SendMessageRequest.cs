namespace Solo.Ai.Client;

public sealed record SendMessageRequest(int ContractVersion, Guid MessageId, string Text);

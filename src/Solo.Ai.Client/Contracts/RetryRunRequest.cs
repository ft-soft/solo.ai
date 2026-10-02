namespace Solo.Ai.Client;

public sealed record RetryRunRequest(int ContractVersion, Guid RetryId);

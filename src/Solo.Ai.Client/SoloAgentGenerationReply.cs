namespace Solo.Ai.Client;

public sealed record SoloAgentGenerationReply(int ContractVersion, SoloAgentResponse? Result, string? Error);

namespace Solo.Ai.Client;

public sealed record SoloAiResponse<T>(int ContractVersion, T? Data, string? Error) where T : class;

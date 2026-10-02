namespace Solo.Ai.Visograph;

public sealed record ModelGenerationRequest(
    int ContractVersion,
    Guid RequestId,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<ModelMessage> Messages,
    ModelResponseFormat ResponseFormat);

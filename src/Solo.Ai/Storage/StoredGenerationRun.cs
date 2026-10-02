using Solo.Ai.Client;

namespace Solo.Ai.Storage;

public sealed record StoredGenerationRun(string Owner, SoloAiRun Run);

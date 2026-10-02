namespace Solo.Ai.Api;

public sealed class SoloAiGenerationOptions
{
    public bool Enabled { get; init; }
    public int MaximumParallelRuns { get; init; } = 4;
    public int MaximumPendingRuns { get; init; } = 100;
    public int FinalizationAttempts { get; init; } = 3;

    internal void Validate()
    {
        if (MaximumParallelRuns is < 1 or > 64 || MaximumPendingRuns is < 1 or > 10000 ||
            FinalizationAttempts is < 1 or > 10)
            throw new InvalidOperationException("Invalid SoloAiGeneration configuration.");
    }
}

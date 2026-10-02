using Solo.Ai.Visograph;

namespace Solo.Ai;

public interface ISoloAgentModelPipeline
{
    Task<string> GenerateAsync(Guid runId, IReadOnlyList<ModelMessage> history, CancellationToken cancellationToken);
}

using Solo.Ai.Client;

namespace Solo.Ai;

public interface ISoloAgentModelPipeline
{
    Task<SoloAgentResponse> GenerateAsync(PreparedGenerationInput input, CancellationToken cancellationToken);
}

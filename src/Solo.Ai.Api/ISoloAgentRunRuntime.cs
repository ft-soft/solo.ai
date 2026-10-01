using Solo.Ai.Client;

namespace Solo.Ai.Api;

/// <summary>Accept one attempt, prepare its server-owned history and finish its lifecycle.</summary>
public interface ISoloAgentRunRuntime
{
    Task<bool> TryAcceptAsync(Guid owner, PreparedGenerationInput input, CancellationToken cancellationToken);
    PreparedGenerationInput PrepareGeneration(PreparedGenerationInput input);
    void Complete(PreparedGenerationInput input, SoloAgentResponse? result);
}

using System.Security.Claims;
using Solo.Ai.Client;

namespace Solo.Ai.Api;

/// <summary>Authorize one attempt, prepare its server-owned history and finish its lifecycle.</summary>
public interface ISoloAgentRunAuthorizer
{
    Task<bool> AuthorizeAndAcceptAsync(ClaimsPrincipal sender, PreparedGenerationInput input, CancellationToken cancellationToken);
    PreparedGenerationInput PrepareGeneration(PreparedGenerationInput input);
    void Complete(PreparedGenerationInput input, SoloAgentResponse? result);
}

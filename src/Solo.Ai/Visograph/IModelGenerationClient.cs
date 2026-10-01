
namespace Solo.Ai.Visograph;

public interface IModelGenerationClient
{
    Task<ModelGenerationResponse> GenerateAsync(ModelGenerationRequest request, CancellationToken cancellationToken);
}

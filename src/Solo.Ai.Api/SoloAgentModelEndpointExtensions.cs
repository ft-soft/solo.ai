using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solo.Ai.Visograph;
using Solo.Toolbox.Extensions;

namespace Solo.Ai.Api;

public static class SoloAgentModelEndpointExtensions
{
    public static IServiceCollection AddSoloAgentModelEndpoint(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddConfiguration<SoloAgentModelApiOptions>(configuration);
        services.AddConfiguration<SoloAgentModelOptions>(configuration);
        services.AddConfiguration<ModelClientOptions>(configuration);
        services.TryAddSingleton<IModelGenerationClient, VisographModelClient>();
        services.TryAddScoped<DocumentRecommendationValidator>();
        services.TryAddScoped<ISoloAgentModelPipeline, SoloAgentModelPipeline>();
        services.AddScoped<SoloAgentModelEndpoint>();
        return services;
    }

    public static IEndpointConventionBuilder MapSoloAgentModelEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost(SoloAgentModelEndpoint.Path,
            (Microsoft.AspNetCore.Http.HttpContext context, SoloAgentModelEndpoint endpoint) => endpoint.GenerateAsync(context))
            .AllowAnonymous();
    }
}

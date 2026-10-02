using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Solo.Ai.Client;

namespace Solo.Ai.Api;

public static class SoloAiChatEndpointExtensions
{
    public static IServiceCollection AddSoloAiApi(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("SoloAiApi").Get<SoloAiApiOptions>() ?? new();
        options.Validate();
        services.AddSingleton(options);
        services.AddSoloAiGeneration(configuration);
        return services;
    }

    public static RouteGroupBuilder MapSoloAiChatEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/" + SoloAiProtocol.Endpoint).RequireAuthorization(ButlerAuthenticationExtensions.Policy);
        group.MapPost("/", SoloAiChatEndpoints.CreateAsync);
        group.MapGet("/current", SoloAiChatEndpoints.GetCurrent);
        group.MapGet("/{chatId}", SoloAiChatEndpoints.GetChat);
        group.MapGet("/{chatId}/messages", SoloAiChatEndpoints.GetMessages);
        group.MapPatch("/{chatId}", SoloAiChatEndpoints.RenameAsync);
        group.MapDelete("/{chatId}", SoloAiChatEndpoints.Delete);
        group.MapPost("/{chatId}/messages", SoloAiChatEndpoints.SendAsync);
        group.MapGet("/{chatId}/runs/{runId}", SoloAiChatEndpoints.GetRun);
        group.MapPost("/{chatId}/runs/{runId}/cancel", SoloAiChatEndpoints.Cancel);
        group.MapPost("/{chatId}/messages/{messageId}/retry", SoloAiChatEndpoints.RetryAsync);
        return group;
    }
}

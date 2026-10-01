using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Solo.Ai.Transcription;

public static class TranscriptionExtensions
{
    public static IServiceCollection AddTranscription(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TranscriptionOptions>().Bind(configuration.GetSection("Transcription"))
            .Validate(value => value.NumThreads is >= 1 and <= 32, "NumThreads must be between 1 and 32.")
            .Validate(value => !value.Enabled || (File.Exists(value.ModelPath) && File.Exists(value.TokensPath) && File.Exists(value.VadModelPath)),
                "Enabled transcription requires existing ModelPath, TokensPath and VadModelPath.")
            .Validate(value => !value.Enabled || !string.IsNullOrWhiteSpace(value.FfmpegPath), "FfmpegPath is required.")
            .ValidateOnStart();
        services.AddSingleton<AudioDecoder>();
        services.AddSingleton<GigaAmTranscriber>();
        services.AddSingleton<TranscriptionEndpoint>();
        return services;
    }

    public static IEndpointConventionBuilder MapTranscription(this IEndpointRouteBuilder endpoints, string authenticationPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationPolicy);
        return endpoints.MapPost("/api/transcription",
            (HttpContext context, TranscriptionEndpoint endpoint) => endpoint.TranscribeAsync(context))
            .RequireAuthorization(authenticationPolicy);
    }
}

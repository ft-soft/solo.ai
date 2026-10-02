using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solo.Ai.Storage;
using Solo.Ai.Visograph;
using Solo.Toolbox.Extensions;

namespace Solo.Ai.Api;

public static class SoloAiGenerationExtensions
{
    public static IServiceCollection AddSoloAiGeneration(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("SoloAiGeneration").Get<SoloAiGenerationOptions>() ?? new();
        options.Validate();
        services.AddSingleton(options);
        services.TryAddSingleton<SoloAgentRunRuntime>();
        if (!options.Enabled) return services;
        var storage = configuration.GetSection("SoloAiStorage").Get<SqliteChatOptions>() ?? new();
        var model = configuration.GetSection("SoloAgentModel").Get<SoloAgentModelOptions>() ?? new();
        var client = configuration.GetSection("VisographModelClient").Get<ModelClientOptions>() ?? new();
        if (!storage.IsValid() || !model.IsValid() || !client.IsValid())
            throw new InvalidOperationException("Generation requires valid storage and model configuration.");
        services.AddConfiguration<SoloAgentModelOptions>(configuration);
        services.AddConfiguration<ModelClientOptions>(configuration);
        services.TryAddSingleton<IModelGenerationClient, VisographModelClient>();
        services.TryAddSingleton<ISoloAgentModelPipeline, SoloAgentModelPipeline>();
        services.AddSingleton<SqliteGenerationQueue>();
        services.AddSingleton<SoloAiRunExecutor>();
        services.AddHostedService<SoloAiGenerationWorker>();
        return services;
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Solo.Ai.Storage;
using Solo.Toolbox.Extensions;

namespace Solo.Ai.Api;

public static class SoloAiStorageExtensions
{
    public static IServiceCollection AddSoloAiStorage(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("SoloAiStorage").Get<SqliteChatOptions>() ?? new();
        if (configuration["SoloAiStorage:ChatRetentionDays"] == "" ||
            options.ChatRetentionDays is <= 0 || options.Enabled && !options.IsValid())
            throw new InvalidOperationException("Invalid SoloAiStorage configuration.");
        services.AddConfiguration<SqliteChatOptions>(configuration);
        services.TryAddSingleton<SoloAgentRunRuntime>();
        services.AddSingleton<SqliteChatDatabase>();
        services.AddSingleton<SqliteChatStore>();
        services.AddSingleton<SqliteRunStore>();
        services.AddSingleton<SqliteRunTransitions>();
        services.AddSingleton<SqliteChatRetention>();
        services.AddSingleton(provider => new SoloAiHealth(provider.GetRequiredService<SoloAgentRunRuntime>(),
            options.Enabled ? provider.GetRequiredService<SqliteChatDatabase>() : null));
        if (options.Enabled) services.AddHostedService<SoloAiStorageWorker>();
        return services;
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Solo.Toolbox.Extensions;

namespace Solo.Ai.Client;

public static class SoloAiClientExtensions
{
    public static IHttpClientBuilder AddSoloAiClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddConfiguration<SoloAiClientOptions>(configuration);
        services.AddTransient<SoloAiClient>();
        return services.AddHttpClient(SoloAiClient.ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    }
}

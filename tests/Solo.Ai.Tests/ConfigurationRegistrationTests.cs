using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Solo.Ai.Api;
using Solo.Ai.Client;
using Solo.Ai.Visograph;
using Solo.Toolbox.Configurations;
using Solo.Toolbox.Extensions;
using Xunit;

namespace Solo.Ai.Tests;

public sealed class ConfigurationRegistrationTests
{
    [Theory]
    [InlineData("MaximumParallelRuns", "0")]
    [InlineData("MaximumPendingRuns", "0")]
    [InlineData("FinalizationAttempts", "0")]
    [InlineData("Enabled", "true")]
    public void RejectInvalidGenerationLimitsOrMissingDependencies(string setting, string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SoloAiGeneration:" + setting] = value,
        }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddSoloAiApi(config));
    }

    [Fact]
    public void RegisterChatClientWithoutRedirectsCookiesOrAutomaticRetries()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSoloAiClient(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<SoloAiClient>());
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(SoloAiClient.ClientName);
        while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;
        var transport = Assert.IsType<HttpClientHandler>(handler);
        Assert.False(transport.AllowAutoRedirect);
        Assert.False(transport.UseCookies);
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(SoloAiClient.ClientName);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Fact]
    public void BindClientConfigurationFromItsDeclaredSection()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SoloAgent:Client:Enabled"] = "true",
            ["SoloAgent:Client:BaseUri"] = "https://solo-ai.test/",
            ["SoloAgent:Client:RequestTimeout"] = "00:00:04",
            ["SoloAgent:Client:TotalDeadline"] = "00:00:05",
            ["SoloAgent:Client:MaxRequestBytes"] = "32768",
            ["SoloAgent:Client:MaxResponseBytes"] = "524288",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConfiguration<SoloAiClientOptions>(configuration);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<SoloAiClientOptions>();

        Assert.True(options.IsValid());
        Assert.Equal(new Uri("https://solo-ai.test/"), options.BaseUri);
        Assert.Same(options, provider.GetRequiredService<IOptions<SoloAiClientOptions>>().Value);
        Assert.Contains(provider.GetServices<ICustomConfiguration>(), item => ReferenceEquals(options, item));
    }

    [Fact]
    public void RegisterEndpointConfigurationsThroughToolbox()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SoloAgentModelApi:MaxRequestBytes"] = "32768",
            ["SoloAgentModel:ModelTimeout"] = "00:00:04",
            ["VisographModelClient:BaseUri"] = "https://visograph.test/",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSoloAgentModelEndpoint(configuration);
        using var provider = services.BuildServiceProvider();
        var api = provider.GetRequiredService<SoloAgentModelApiOptions>();
        var model = provider.GetRequiredService<SoloAgentModelOptions>();
        var client = provider.GetRequiredService<ModelClientOptions>();

        Assert.Equal(32768, api.MaxRequestBytes);
        Assert.Equal(TimeSpan.FromSeconds(4), model.ModelTimeout);
        Assert.Equal(new Uri("https://visograph.test/"), client.BaseUri);
        Assert.Same(api, provider.GetRequiredService<IOptions<SoloAgentModelApiOptions>>().Value);
        Assert.Same(model, provider.GetRequiredService<IOptions<SoloAgentModelOptions>>().Value);
        Assert.Same(client, provider.GetRequiredService<IOptions<ModelClientOptions>>().Value);
        Assert.Equal(3, provider.GetServices<ICustomConfiguration>().Count());
    }
}

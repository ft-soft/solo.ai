using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Solo.Ai.Api;
using Solo.Ai.Client;
using Xunit;

namespace Solo.Ai.Tests;

public sealed class SoloAiLoggingTests
{
    [Fact]
    public async Task SuppressFrameworkPayloadsAndCatchRawExceptionEvenWithTraceConfiguration()
    {
        var logs = new GenerationLog();
        using var host = await new HostBuilder()
            .ConfigureLogging(logging => logging.AddConfiguration(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["LogLevel:Default"] = "Trace", ["LogLevel:Microsoft"] = "Trace" }).Build())
                .AddProvider(logs).AddSoloAiSafeLogging())
            .ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services => services.AddRouting())
                .Configure(app =>
                {
                    app.UseMiddleware<SoloAiExceptionMiddleware>();
                    app.Run(_ => throw new InvalidOperationException("exception-secret-marker"));
                })).StartAsync(TestContext.Current.CancellationToken);
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer token-marker");
        using var response = await client.PostAsync("/?q=query-marker", new StringContent("body-marker"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("unavailable", body);
        Assert.DoesNotContain("marker", body);
        Assert.NotEmpty(logs.Entries);
        Assert.DoesNotContain(logs.Entries, entry => entry.Contains("marker"));
    }

    [Fact]
    public async Task SdkDisablesFactoryLoggingEvenWhenConsumerEnablesTrace()
    {
        var logs = new GenerationLog();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSoloAiClient(new ConfigurationBuilder().Build())
            .ConfigurePrimaryHttpMessageHandler(() => new SoloAiHttpFixture((request, _) =>
            {
                Assert.Equal("Bearer token-marker", request.Headers.Authorization!.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    { Content = new StringContent("provider-body-marker") });
            }) { EchoHeaders = false });
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(SoloAiClient.ClientName);
        client.DefaultRequestHeaders.Add("Authorization", "Bearer token-marker");
        using var response = await client.PostAsync("https://synthetic.test/?q=secret-marker",
            new StringContent("text-marker"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(logs.Entries, entry => entry.Contains("marker"));
    }
}

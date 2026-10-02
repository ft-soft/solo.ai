using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Solo.Ai.Api;
using Solo.Ai.Client;
using Solo.Ai.Visograph;
using Xunit;

namespace Solo.Ai.Tests;

internal sealed class GenerationFixture : IAsyncDisposable
{
    public SqliteStorageFixture Storage { get; } = new();
    public GenerationModelClient Model { get; } = new() { IgnoreCancellation = true };
    public GenerationLog Logs { get; } = new();
    private IHost? host;
    public SoloAgentRunRuntime Runtime => host!.Services.GetRequiredService<SoloAgentRunRuntime>();

    public static Dictionary<string, string?> ModelConfiguration() => new()
    {
        ["SoloAiGeneration:Enabled"] = "true",
        ["SoloAgentModel:Enabled"] = "true", ["SoloAgentModel:MaxRequestBytes"] = "100000",
        ["SoloAgentModel:TotalDeadline"] = "00:00:20", ["SoloAgentModel:ModelTimeout"] = "00:00:20",
        ["VisographModelClient:Enabled"] = "true", ["VisographModelClient:BaseUri"] = "https://visograph.test/",
        ["VisographModelClient:ApiKey"] = "synthetic-service-key", ["VisographModelClient:RequestTimeout"] = "00:00:20",
        ["VisographModelClient:MaxRequestBytes"] = "100000", ["VisographModelClient:MaxResponseBytes"] = "100000",
    };

    public async Task StartAsync(Dictionary<string, string?>? changes = null)
    {
        var config = ModelConfiguration();
        config["SoloAiStorage:Enabled"] = "true";
        config["SoloAiStorage:DatabasePath"] = Storage.DatabasePath;
        foreach (var pair in changes ?? []) config[pair.Key] = pair.Value;
        host = new HostBuilder().ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(config))
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<IModelGenerationClient>(Model);
                services.AddLogging(logging => logging.AddProvider(Logs));
                services.AddSoloAiStorage(context.Configuration);
                services.AddSoloAiApi(context.Configuration);
            }).Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
    }

    public async Task StopAsync()
    {
        if (host is null) return;
        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();
        host = null;
    }

    public async Task<SoloAiRun> WaitForStateAsync(SoloAiRun run, string state, string owner = SqliteStorageFixture.Owner)
    {
        SoloAiRun result = run;
        await WaitAsync(() => (result = Storage.Runs.GetRun(owner, run.ChatId, run.RunId)).State == state);
        return result;
    }

    public static async Task WaitAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Storage.Dispose();
    }
}

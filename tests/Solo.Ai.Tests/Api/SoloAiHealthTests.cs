using System.Net;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Solo.Ai.Tests;

public sealed class SoloAiHealthTests
{
    [Theory]
    [InlineData("CREATE TRIGGER FailHealth BEFORE UPDATE ON StorageHealth BEGIN SELECT RAISE(ABORT, 'private-disk-failure'); END;")]
    [InlineData("PRAGMA user_version=999;")]
    [InlineData("DELETE FROM StorageHealth;")]
    public async Task CheckActualStorageAndLatchReadinessWithoutCallingModel(string fault)
    {
        var model = new GenerationModelClient();
        using var api = await ButlerApiFixture.StartAsync(modelClient: model);
        using var ready = await api.Client.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        api.Storage.Execute(fault);
        using var unavailable = await api.Client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal("{\"status\":\"unavailable\"}", await unavailable.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var live = await api.Client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.False(api.Runtime.IsReady);
        Assert.Empty(model.Calls);
        Assert.DoesNotContain(api.Logs.Entries, entry => entry.Contains("private-disk-failure"));
    }

    [Fact]
    public async Task DisabledStorageIsAliveButNeverReady()
    {
        using var api = await ButlerApiFixture.StartAsync(new() { ["SoloAiStorage:Enabled"] = "false" });
        using var ready = await api.Client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        using var live = await api.Client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task RejectFutureSchemaBeforeServingRequests()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ButlerApiFixture.StartAsync(
            prepareStorage: db => db.Execute("PRAGMA user_version=999;")));
    }

    [Fact]
    public async Task DetectReadOnlyDiskWithARealWriteProbe()
    {
        await using var fixture = new GenerationFixture();
        await fixture.StartAsync();
        var path = fixture.Storage.DatabasePath;
        var attributes = File.GetAttributes(path);
        try
        {
            File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
            Assert.Throws<SqliteException>(() => fixture.Storage.Database.CheckHealth());
        }
        finally { File.SetAttributes(path, attributes); }
    }

    [Fact]
    public async Task RejectSecondHostOnSameDatabaseAndReleaseLockAfterShutdown()
    {
        await using var first = new GenerationFixture();
        await first.StartAsync();
        await using var second = new GenerationFixture();
        var config = new Dictionary<string, string?> { ["SoloAiStorage:DatabasePath"] = first.Storage.DatabasePath };
        await Assert.ThrowsAsync<IOException>(() => second.StartAsync(config));
        await second.StopAsync();
        await first.StopAsync();
        await second.StartAsync(config);
        Assert.True(second.Runtime.IsReady);
    }
}

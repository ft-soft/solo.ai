using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Solo.Ai.Api;
using Solo.Ai.Storage;
using Xunit;
using static Solo.Ai.Tests.SqliteStorageFixture;

namespace Solo.Ai.Tests;

public sealed class SqliteStorageRegistrationTests
{
    [Fact]
    public void BindStorageOptionsAndReopenPersistedChatAcrossHosts()
    {
        using var fixture = new SqliteStorageFixture();
        IHost BuildHost()
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SoloAiStorage:Enabled"] = "true",
                ["SoloAiStorage:DatabasePath"] = fixture.DatabasePath,
                ["SoloAiStorage:BusyTimeoutSeconds"] = "3",
                ["SoloAiStorage:RunTimeout"] = "00:02:00",
            });
            builder.Services.AddSoloAiStorage(builder.Configuration);
            var host = builder.Build();
            host.Services.GetRequiredService<SqliteChatDatabase>().Initialize();
            return host;
        }
        var chatId = Guid.NewGuid();
        Guid messageId;
        Guid runId;
        using (var first = BuildHost())
        {
            var options = first.Services.GetRequiredService<SqliteChatOptions>();
            Assert.True(options.IsValid());
            Assert.Equal(3, options.BusyTimeoutSeconds);
            first.Services.GetRequiredService<SqliteChatStore>().CreateChat(Owner, chatId);
            var run = first.Services.GetRequiredService<SqliteRunStore>().SendMessage(Owner, chatId,
                new(2, Guid.NewGuid(), "across hosts"));
            messageId = run.UserMessageId;
            runId = run.RunId;
            Assert.Equal(TimeSpan.FromMinutes(2), run.DeadlineAt - run.CreatedAt);
        }
        using var second = BuildHost();
        var chats = second.Services.GetRequiredService<SqliteChatStore>();
        Assert.Equal(chatId, chats.GetCurrentChat(Owner)!.ChatId);
        Assert.Single(chats.GetMessages(Owner, chatId).Messages);
        Assert.Equal(runId, second.Services.GetRequiredService<SqliteRunStore>()
            .SendMessage(Owner, chatId, new(2, messageId, "across hosts")).RunId);
    }

    [Fact]
    public void DisabledConfigurationDoesNotCreateStorageAndEnabledInvalidConfigurationFails()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSoloAiStorage(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        Assert.False(provider.GetRequiredService<SqliteChatOptions>().Enabled);
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<SqliteChatDatabase>());
        using var fixture = new SqliteStorageFixture();
        foreach (var options in new[]
        {
            new SqliteChatOptions { Enabled = true },
            new SqliteChatOptions { Enabled = true, DatabasePath = fixture.DatabasePath, BusyTimeoutSeconds = 0 },
            new SqliteChatOptions { Enabled = true, DatabasePath = fixture.DatabasePath, RunTimeout = TimeSpan.Zero },
        }) Assert.Throws<InvalidOperationException>(() => new SqliteChatDatabase(options));
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Solo.Ai.Api;
using Solo.Ai.Client;
using Solo.Ai.Storage;
using Xunit;
using static Solo.Ai.Tests.SqliteStorageFixture;

namespace Solo.Ai.Tests;

public sealed class SqliteChatRetentionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(int.MaxValue)]
    public void PreserveChatsWithUnlimitedOrVeryLongRetention(int? days)
    {
        using var db = new SqliteStorageFixture();
        var id = db.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        AgeChat(db, id, 1);
        Assert.Equal(0, CreateRetention(db, days).DeleteExpiredChats(DateTimeOffset.UtcNow));
        Assert.Equal(id, db.Chats.GetCurrentChat(Owner)!.ChatId);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("invalid")]
    [InlineData("")]
    [InlineData("2147483648")]
    public void RejectInvalidRetentionDuringRegistration(string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SoloAiStorage:ChatRetentionDays"] = value,
        }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddSoloAiStorage(config));
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void DeleteEmptyChatAtExactUtcBoundaryDespiteReadAndRename(long offsetTicks, int expected)
    {
        using var db = new SqliteStorageFixture();
        var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5));
        var id = db.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        AgeChat(db, id, now.UtcTicks - TimeSpan.TicksPerDay + offsetTicks);
        db.Chats.RenameChat(Owner, id, "recent rename");
        db.Chats.GetCurrentChat(Owner);
        db.Chats.GetMessages(Owner, id);
        Assert.Equal(expected, CreateRetention(db).DeleteExpiredChats(now));
        Assert.Equal((long)expected, db.Execute("SELECT COUNT(*) FROM DeletedChat;"));
        if (expected == 1)
        {
            Assert.Null(db.Chats.GetCurrentChat(Owner));
            AssertError("not_found", () => db.Chats.CreateChat(Owner, id));
            Assert.NotEqual(id, db.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId);
        }
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("running")]
    [InlineData("cancellation_requested")]
    public void KeepActiveRunEvenWhenChatIsExpired(string state)
    {
        using var db = new SqliteStorageFixture();
        var id = db.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        var run = db.Send(id);
        if (state == "running") db.Transitions.TryStartRun(Owner, id, run.RunId);
        if (state == "cancellation_requested") db.Transitions.RequestCancellation(Owner, id, run.RunId);
        AgeChat(db, id, DateTimeOffset.UtcNow.AddDays(-2).Ticks, withMessage: true);
        Assert.Equal(0, CreateRetention(db).DeleteExpiredChats(DateTimeOffset.UtcNow));
        Assert.Equal(state, db.Runs.GetRun(Owner, id, run.RunId).State);
    }

    [Fact]
    public void DedupDoesNotExtendAgeAndDeletionRemovesMessagesRunsTogether()
    {
        using var db = new SqliteStorageFixture();
        var id = db.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        var run = db.Send(id);
        db.Complete(id, run);
        AgeChat(db, id, DateTimeOffset.UtcNow.AddDays(-2).Ticks, withMessage: true);
        db.Runs.SendMessage(Owner, id, new(2, run.UserMessageId, "hello"));
        Assert.Equal(1, CreateRetention(db).DeleteExpiredChats(DateTimeOffset.UtcNow));
        Assert.Equal(0L, db.Execute("SELECT COUNT(*) FROM Message;"));
        Assert.Equal(0L, db.Execute("SELECT COUNT(*) FROM GenerationRun;"));
        Assert.Equal(1L, db.Execute("SELECT COUNT(*) FROM DeletedChat;"));
    }

    [Fact]
    public void LastMessageRatherThanCreationControlsAgeAndFailedDeletionRollsBack()
    {
        using var db = new SqliteStorageFixture();
        var id = db.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        var run = db.Send(id);
        db.Fail(id, run);
        db.Execute("UPDATE Chat SET CreatedAt=$old;", ("$old", DateTimeOffset.UtcNow.AddDays(-10).Ticks));
        var retention = CreateRetention(db);
        Assert.Equal(0, retention.DeleteExpiredChats(DateTimeOffset.UtcNow));
        AgeChat(db, id, DateTimeOffset.UtcNow.AddDays(-2).Ticks, withMessage: true);
        db.Execute("CREATE TRIGGER FailRetention BEFORE INSERT ON DeletedChat BEGIN SELECT RAISE(ABORT, 'private'); END;");
        Assert.Throws<SqliteException>(() => retention.DeleteExpiredChats(DateTimeOffset.UtcNow));
        Assert.Single(db.Chats.GetMessages(Owner, id).Messages);
        Assert.Equal(0L, db.Execute("SELECT COUNT(*) FROM DeletedChat;"));
    }

    [Fact]
    public async Task SerializeCleanupAgainstNewMessageWithoutLosingAnAcceptedSend()
    {
        using var db = new SqliteStorageFixture();
        var retention = CreateRetention(db);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var id = db.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
            AgeChat(db, id, DateTimeOffset.UtcNow.AddDays(-2).Ticks);
            var results = await Race(() =>
            {
                try { db.Send(id); return true; }
                catch (SoloAiExchangeException error) { Assert.Equal("not_found", error.Code); return false; }
            }, () => { retention.DeleteExpiredChats(DateTimeOffset.UtcNow); return false; });
            if (results[0])
            {
                Assert.Single(db.Chats.GetMessages(Owner, id).Messages);
                Assert.Equal(0, retention.DeleteExpiredChats(DateTimeOffset.UtcNow));
                db.Chats.DeleteChat(Owner, id);
            }
            else Assert.Null(db.Chats.GetCurrentChat(Owner));
        }
    }

    [Fact]
    public async Task ApplyConfiguredRetentionBeforeHostStarts()
    {
        await using var fixture = new GenerationFixture();
        var id = fixture.Storage.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        AgeChat(fixture.Storage, id, DateTimeOffset.UtcNow.AddDays(-2).Ticks);
        await fixture.StartAsync(new() { ["SoloAiStorage:ChatRetentionDays"] = "1" });
        Assert.True(fixture.Runtime.IsReady);
        Assert.Null(fixture.Storage.Chats.GetCurrentChat(Owner));
        Assert.Empty(fixture.Model.Calls);
    }

    private static SqliteChatRetention CreateRetention(SqliteStorageFixture db, int? days = 1) =>
        new(db.Database, new() { ChatRetentionDays = days });

    private static void AgeChat(SqliteStorageFixture db, Guid id, long ticks, bool withMessage = false) =>
        db.Execute("UPDATE Chat SET CreatedAt=$age, LastMessageAt=$last WHERE Id=$id;",
            ("$age", ticks), ("$last", withMessage ? ticks : null), ("$id", id));
}

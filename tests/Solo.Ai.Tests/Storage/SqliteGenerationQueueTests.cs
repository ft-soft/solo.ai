using Solo.Ai.Client;
using Solo.Ai.Storage;
using Xunit;
using static Solo.Ai.Tests.SqliteStorageFixture;

namespace Solo.Ai.Tests;

public sealed class SqliteGenerationQueueTests
{
    [Fact]
    public void UpgradeVersionOneWithoutChangingAcceptedWork()
    {
        using var db = new SqliteStorageFixture();
        var run = db.Send(db.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId);
        db.Execute("DROP INDEX GenerationQueue; DROP TABLE StorageHealth; PRAGMA user_version=1;");
        db.Database.Initialize();
        Assert.Equal((long)SqliteChatDatabase.SchemaVersion, db.Execute("PRAGMA user_version;"));
        Assert.Equal(run, db.Runs.GetRun(Owner, run.ChatId, run.RunId));
        Assert.Equal("GenerationQueue", db.Execute("SELECT name FROM sqlite_master WHERE type='index' AND name='GenerationQueue';"));
    }

    [Fact]
    public async Task BoundQueueAtomicallyAcrossOwnersAndCheckDuplicatesBeforeCapacity()
    {
        using var db = new SqliteStorageFixture();
        var alice = db.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        var bob = db.Chats.CreateChat(OtherOwner, Guid.NewGuid()).Chat.ChatId;
        var request = new SendMessageRequest(2, Guid.NewGuid(), "hello");
        SoloAiRun? TrySend(string owner, Guid chat, SendMessageRequest message)
        {
            try { return new SqliteRunStore(db.Reopen()).SendMessage(owner, chat, message, maximumPending: 1); }
            catch (SoloAiExchangeException error) { Assert.Equal("capacity_exceeded", error.Code); return null; }
        }
        var results = await Race(() => TrySend(Owner, alice, request), () => TrySend(OtherOwner, bob, request));
        Assert.Single(results.OfType<SoloAiRun>());
        var winner = results.OfType<SoloAiRun>().Single();
        var owner = winner.ChatId == alice ? Owner : OtherOwner;
        Assert.Equal(winner, db.Runs.SendMessage(owner, winner.ChatId, request, maximumPending: 1, accepting: false));
        AssertError("idempotency_conflict", () => db.Runs.SendMessage(owner, winner.ChatId, request with { Text = "changed" }, maximumPending: 1));
        Assert.Equal(1L, db.Execute("SELECT COUNT(*) FROM Message;"));
        db.Transitions.RequestCancellation(owner, winner.ChatId, winner.RunId);
        new SqliteGenerationQueue(db.Database).SettleWaitingRuns();
        var retryRequest = new RetryRunRequest(2, Guid.NewGuid());
        var retry = db.Runs.RetryRun(owner, winner.ChatId, winner.UserMessageId, retryRequest, maximumPending: 1);
        Assert.Equal(retry, db.Runs.RetryRun(owner, winner.ChatId, winner.UserMessageId, retryRequest, maximumPending: 1, accepting: false));
    }

    [Fact]
    public void ReadAllOrderedHistoryIncludingFailedUserMessagesAndCurrentTextExactlyOnce()
    {
        using var db = new SqliteStorageFixture();
        var chat = db.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        var first = db.Send(chat, "first");
        db.Complete(chat, first, "answer");
        var failed = db.Send(chat, "failed-user");
        db.Fail(chat, failed);
        var request = new RetryRunRequest(2, Guid.NewGuid());
        var retry = db.Runs.RetryRun(Owner, chat, failed.UserMessageId, request);
        db.Transitions.TryStartRun(Owner, chat, retry.RunId);
        var queue = new SqliteGenerationQueue(db.Database);
        var history = queue.ReadHistory(new(Owner, retry), 100000);
        Assert.Equal(new[] { "first", "answer", "failed-user" }, history.Select(message => message.Content));
        Assert.Equal(new[] { "user", "assistant", "user" }, history.Select(message => message.Role));
        Assert.Throws<Solo.Ai.Visograph.ModelExchangeException>(() => queue.ReadHistory(new(Owner, retry), 1));
        AssertError("not_found", () => queue.ReadHistory(new(OtherOwner, retry), 100000));
        db.Chats.DeleteChat(Owner, chat);
        db.Chats.CreateChat(Owner, Guid.NewGuid());
        AssertError("not_found", () => queue.ReadHistory(new(Owner, retry), 100000));
    }
}

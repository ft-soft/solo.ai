using Solo.Ai.Client;
using Solo.Ai.Storage;
using Xunit;
using static Solo.Ai.Tests.SqliteStorageFixture;

namespace Solo.Ai.Tests;

public sealed class SqliteChatStorageTests
{
    [Fact]
    public void PersistCurrentChatHistoryAndTitleWithoutResettingActivity()
    {
        using var fixture = new SqliteStorageFixture();
        Assert.Null(fixture.Chats.GetCurrentChat(Owner));
        var id = Guid.NewGuid();
        var created = fixture.Chats.CreateChat(Owner, id);
        Assert.True(created.Created);
        Assert.Equal("Новый чат", created.Chat.Title);
        Assert.Null(created.Chat.LastMessageAt);
        var first = fixture.Send(id);
        var completed = fixture.Complete(id, first);
        var activity = fixture.Chats.GetChat(Owner, id).LastMessageAt;
        fixture.Chats.RenameChat(Owner, id, "  Личный чат  ");
        var reopened = new SqliteChatStore(fixture.Reopen());
        var same = reopened.CreateChat(Owner, Guid.NewGuid());
        Assert.False(same.Created);
        Assert.Equal(id, same.Chat.ChatId);
        Assert.Equal("  Личный чат  ", same.Chat.Title);
        Assert.Equal(activity, same.Chat.LastMessageAt);
        Assert.Equal(completed, same.Chat.LatestRun);
        Assert.Equal(same.Chat, reopened.GetCurrentChat(Owner));
        var page = reopened.GetMessages(Owner, id);
        Assert.Equal(new[] { "hello", "answer" }, page.Messages.Select(message => message.Text));
        Assert.Equal(new long[] { 1, 2 }, page.Messages.Select(message => message.Sequence));
        SoloAiValidation.ValidateChat(same.Chat);
        SoloAiValidation.ValidatePage(page, id, null, 50);
        Assert.Equal("ok", fixture.Execute("PRAGMA integrity_check;"));
        Assert.Null(fixture.Execute("PRAGMA foreign_key_check;"));
    }

    [Fact]
    public async Task ConcurrentCreatesReturnOneCanonicalChatAcrossIndependentStoreInstances()
    {
        using var fixture = new SqliteStorageFixture();
        var second = new SqliteChatStore(fixture.Reopen());
        var results = await Race(() => fixture.Chats.CreateChat(Owner, Guid.NewGuid()),
            () => second.CreateChat(Owner, Guid.NewGuid()));
        Assert.Single(results, result => result.Created);
        Assert.Equal(results[0].Chat, results[1].Chat);
        Assert.Equal(1L, fixture.Execute("SELECT COUNT(*) FROM Chat;"));
    }

    [Fact]
    public void ScopeEveryChatOperationToOwnerAndRejectForeignRequestedCreateId()
    {
        using var fixture = new SqliteStorageFixture();
        var id = fixture.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        fixture.Send(id);
        Assert.Null(fixture.Chats.GetCurrentChat(OtherOwner));
        var other = fixture.Chats.CreateChat(OtherOwner, Guid.NewGuid()).Chat;
        AssertError("not_found", () => fixture.Chats.CreateChat(OtherOwner, id));
        AssertError("not_found", () => fixture.Chats.GetChat(OtherOwner, id));
        AssertError("not_found", () => fixture.Chats.GetMessages(OtherOwner, id));
        AssertError("not_found", () => fixture.Chats.RenameChat(OtherOwner, id, "stolen"));
        AssertError("not_found", () => fixture.Chats.DeleteChat(OtherOwner, id));
        Assert.Equal(other, fixture.Chats.GetCurrentChat(OtherOwner));
        Assert.Single(fixture.Chats.GetMessages(Owner, id).Messages);
        Assert.Equal(0L, fixture.Execute("SELECT COUNT(*) FROM DeletedChat;"));
    }

    [Fact]
    public void PageNewestMessagesInAscendingSequenceWithStableExclusiveCursor()
    {
        using var fixture = new SqliteStorageFixture();
        var id = fixture.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        for (var index = 0; index < 3; index++) fixture.Complete(id, fixture.Send(id, $"question {index}"));
        var page = fixture.Chats.GetMessages(Owner, id, limit: 2);
        Assert.Equal(new long[] { 5, 6 }, page.Messages.Select(message => message.Sequence));
        Assert.Equal(SoloAiMessageCursor.Create(id, 5), page.NextCursor);
        fixture.Send(id, "new arrival");
        var earlier = fixture.Chats.GetMessages(Owner, id, page.NextCursor, 2);
        Assert.Equal(new long[] { 3, 4 }, earlier.Messages.Select(message => message.Sequence));
        var oldest = fixture.Chats.GetMessages(Owner, id, earlier.NextCursor, 2);
        Assert.Equal(new long[] { 1, 2 }, oldest.Messages.Select(message => message.Sequence));
        Assert.Null(oldest.NextCursor);
        var empty = fixture.Chats.GetMessages(Owner, id, SoloAiMessageCursor.Create(id, 1));
        Assert.Empty(empty.Messages);
        Assert.Null(empty.NextCursor);
        AssertError("validation_failed", () => fixture.Chats.GetMessages(Owner, id, limit: 201));
        AssertError("validation_failed", () => fixture.Chats.GetMessages(Owner, id, SoloAiMessageCursor.Create(Guid.NewGuid(), 4)));
    }

    [Fact]
    public void DeleteRemovesAllTextAndRunsButKeepsDurableTombstoneAndProtectsNewChat()
    {
        using var fixture = new SqliteStorageFixture();
        var id = fixture.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        var first = fixture.Send(id, "deleted secret");
        fixture.Complete(id, first, "deleted response");
        var active = fixture.Send(id);
        fixture.Chats.DeleteChat(Owner, id);
        Assert.Null(fixture.Chats.GetCurrentChat(Owner));
        Assert.Equal(0L, fixture.Execute("SELECT COUNT(*) FROM Message;"));
        Assert.Equal(0L, fixture.Execute("SELECT COUNT(*) FROM GenerationRun;"));
        Assert.Equal(id.ToString("D"), fixture.Execute("SELECT ChatId FROM DeletedChat;"));
        Assert.IsType<long>(fixture.Execute("SELECT DeletedAt FROM DeletedChat;"));
        var reopened = new SqliteChatStore(fixture.Reopen());
        var replacement = reopened.CreateChat(Owner, Guid.NewGuid()).Chat;
        AssertError("not_found", () => reopened.CreateChat(Owner, id));
        AssertError("not_found", () => reopened.CreateChat(OtherOwner, id));
        AssertError("not_found", () => reopened.DeleteChat(Owner, id));
        AssertError("not_found", () => fixture.Runs.SendMessage(Owner, id, new(2, first.UserMessageId, "deleted secret")));
        AssertError("not_found", () => fixture.Runs.RetryRun(Owner, id, first.UserMessageId, new(2, Guid.NewGuid())));
        AssertError("not_found", () => fixture.Transitions.RequestCancellation(Owner, id, active.RunId));
        AssertError("not_found", () => fixture.Transitions.FinishRun(Owner, id, active.RunId, "completed", assistantText: "late"));
        Assert.Equal(replacement, reopened.GetCurrentChat(Owner));
        Assert.Empty(reopened.GetMessages(Owner, replacement.ChatId).Messages);
    }

    [Fact]
    public void ValidateInputsBeforeStorageMutation()
    {
        using var fixture = new SqliteStorageFixture();
        AssertError("validation_failed", () => fixture.Chats.CreateChat(" ", Guid.NewGuid()));
        AssertError("validation_failed", () => fixture.Chats.CreateChat(Owner, Guid.Empty));
        var id = fixture.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        AssertError("validation_failed", () => fixture.Chats.RenameChat(Owner, id, " "));
        AssertError("limit_exceeded", () => fixture.Chats.RenameChat(Owner, id, new string('я', 257)));
        AssertError("validation_failed", () => fixture.Runs.SendMessage(Owner, id, new(2, Guid.NewGuid(), "\ud800")));
        AssertError("limit_exceeded", () => fixture.Send(id, new string('x', 65537)));
        AssertError("unsupported_contract", () => fixture.Runs.SendMessage(Owner, id, new(1, Guid.NewGuid(), "hello")));
        Assert.Empty(fixture.Chats.GetMessages(Owner, id).Messages);
    }
}

using Solo.Ai.Client;
using Solo.Ai.Storage;
using Xunit;
using static Solo.Ai.Tests.SqliteStorageFixture;

namespace Solo.Ai.Tests;

public sealed class SqliteRunStorageTests
{
    [Fact]
    public void PersistSendAndRetryDedupAcrossConnectionsWithoutExtendingActivity()
    {
        using var fixture = new SqliteStorageFixture();
        var chat = fixture.Chats.CreateChat(Owner, Guid.NewGuid()).Chat;
        var request = new SendMessageRequest(2, Guid.NewGuid(), " exact text ");
        var first = fixture.Runs.SendMessage(Owner, chat.ChatId, request);
        var activity = fixture.Chats.GetChat(Owner, chat.ChatId).LastMessageAt;
        var runs = new SqliteRunStore(fixture.Reopen());
        Assert.Equal(first, runs.SendMessage(Owner, chat.ChatId, request));
        AssertError("idempotency_conflict", () => runs.SendMessage(Owner, chat.ChatId, request with { Text = "exact text" }));
        AssertError("chat_busy", () => fixture.Send(chat.ChatId));
        var failed = fixture.Fail(chat.ChatId, first);
        var retryRequest = new RetryRunRequest(2, Guid.NewGuid());
        var retry = runs.RetryRun(Owner, chat.ChatId, first.UserMessageId, retryRequest);
        Assert.NotEqual(first.RunId, retry.RunId);
        Assert.Equal(failed, runs.SendMessage(Owner, chat.ChatId, request));
        Assert.Equal(retry, new SqliteRunStore(fixture.Reopen()).RetryRun(Owner, chat.ChatId, first.UserMessageId, retryRequest));
        Assert.Equal(activity, fixture.Chats.GetChat(Owner, chat.ChatId).LastMessageAt);
        var message = Assert.Single(fixture.Chats.GetMessages(Owner, chat.ChatId).Messages);
        Assert.Equal(retry, message.Run);
        Assert.Equal(first.UserMessageId, message.MessageId);
        var completed = fixture.Complete(chat.ChatId, retry);
        Assert.Equal(completed, runs.RetryRun(Owner, chat.ChatId, first.UserMessageId, retryRequest));
        Assert.Equal(failed, runs.SendMessage(Owner, chat.ChatId, request));
        SoloAiValidation.ValidateRun(completed, chat.ChatId);
    }

    [Fact]
    public async Task ConcurrentDistinctSendsAcceptExactlyOneMessageAndRun()
    {
        using var fixture = new SqliteStorageFixture();
        var id = fixture.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        var otherStore = new SqliteRunStore(fixture.Reopen());
        string Send(SqliteRunStore store)
        {
            try { store.SendMessage(Owner, id, new(2, Guid.NewGuid(), "racing")); return "accepted"; }
            catch (SoloAiExchangeException exception) { return exception.Code; }
        }
        var results = await Race(() => Send(fixture.Runs), () => Send(otherStore));
        Assert.Equal(new[] { "accepted", "chat_busy" }, results.Order());
        Assert.Equal(1L, fixture.Execute("SELECT COUNT(*) FROM Message;"));
        Assert.Equal(1L, fixture.Execute("SELECT COUNT(*) FROM GenerationRun;"));
    }

    [Fact]
    public async Task ConcurrentDuplicateSendsAndRetriesReturnTheSameDurableOperation()
    {
        using var fixture = new SqliteStorageFixture();
        var id = fixture.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        var otherStore = new SqliteRunStore(fixture.Reopen());
        var request = new SendMessageRequest(2, Guid.NewGuid(), "racing");
        var sends = await Race(() => fixture.Runs.SendMessage(Owner, id, request), () => otherStore.SendMessage(Owner, id, request));
        Assert.Equal(sends[0], sends[1]);
        fixture.Fail(id, sends[0]);
        var retry = new RetryRunRequest(2, Guid.NewGuid());
        var retries = await Race(() => fixture.Runs.RetryRun(Owner, id, request.MessageId, retry),
            () => otherStore.RetryRun(Owner, id, request.MessageId, retry));
        Assert.Equal(retries[0], retries[1]);
        Assert.Equal(1L, fixture.Execute("SELECT COUNT(*) FROM Message;"));
        Assert.Equal(2L, fixture.Execute("SELECT COUNT(*) FROM GenerationRun;"));
    }

    [Fact]
    public async Task ConcurrentDifferentRetryIdsAcceptOnlyOneRun()
    {
        using var fixture = new SqliteStorageFixture();
        var id = fixture.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        var sent = fixture.Send(id);
        fixture.Fail(id, sent);
        string Retry()
        {
            try { new SqliteRunStore(fixture.Reopen()).RetryRun(Owner, id, sent.UserMessageId, new(2, Guid.NewGuid())); return "accepted"; }
            catch (SoloAiExchangeException exception) { return exception.Code; }
        }
        Assert.Equal(new[] { "accepted", "chat_busy" }, (await Race(Retry, Retry)).Order());
        Assert.Equal(2L, fixture.Execute("SELECT COUNT(*) FROM GenerationRun;"));
    }

    [Fact]
    public void RejectRetryOfOlderSuccessfulOrUnchangedOversizedMessageAndConflictingRetryId()
    {
        using var fixture = new SqliteStorageFixture();
        var id = fixture.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        var first = fixture.Send(id);
        fixture.Fail(id, first);
        var retryId = Guid.NewGuid();
        var retry = fixture.Runs.RetryRun(Owner, id, first.UserMessageId, new(2, retryId));
        fixture.Complete(id, retry);
        AssertError("retry_not_allowed", () => fixture.Runs.RetryRun(Owner, id, first.UserMessageId, new(2, Guid.NewGuid())));
        var second = fixture.Send(id, "next");
        fixture.Fail(id, second, "limit_exceeded");
        AssertError("retry_not_allowed", () => fixture.Runs.RetryRun(Owner, id, second.UserMessageId, new(2, Guid.NewGuid())));
        AssertError("retry_not_allowed", () => fixture.Runs.RetryRun(Owner, id, first.UserMessageId, new(2, Guid.NewGuid())));
        AssertError("idempotency_conflict", () => fixture.Runs.RetryRun(Owner, id, second.UserMessageId, new(2, retryId)));
        AssertError("not_found", () => fixture.Runs.RetryRun(Owner, id, Guid.NewGuid(), new(2, Guid.NewGuid())));
    }

    [Fact]
    public void ScopeSendRetryRunReadsAndWritesToOwnerWhileAllowingSameKeysForOtherOwners()
    {
        using var fixture = new SqliteStorageFixture();
        var id = fixture.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        var other = fixture.Chats.CreateChat(OtherOwner, Guid.NewGuid()).Chat.ChatId;
        var request = new SendMessageRequest(2, Guid.NewGuid(), "same key");
        var run = fixture.Runs.SendMessage(Owner, id, request);
        var otherRun = fixture.Runs.SendMessage(OtherOwner, other, request);
        Assert.NotEqual(run.RunId, otherRun.RunId);
        AssertError("not_found", () => fixture.Runs.SendMessage(OtherOwner, id, request));
        AssertError("not_found", () => fixture.Runs.GetRun(OtherOwner, id, run.RunId));
        AssertError("not_found", () => fixture.Runs.GetRun(Owner, other, run.RunId));
        AssertError("not_found", () => fixture.Runs.RetryRun(OtherOwner, id, run.UserMessageId, new(2, Guid.NewGuid())));
        AssertError("not_found", () => fixture.Transitions.TryStartRun(OtherOwner, id, run.RunId));
        AssertError("not_found", () => fixture.Transitions.RequestCancellation(OtherOwner, id, run.RunId));
        AssertError("not_found", () => fixture.Transitions.FinishRun(OtherOwner, id, run.RunId, "failed", "provider_error"));
        fixture.Fail(id, run);
        fixture.Transitions.FinishRun(OtherOwner, other, otherRun.RunId, "failed", "provider_error");
        var retry = new RetryRunRequest(2, Guid.NewGuid());
        var one = fixture.Runs.RetryRun(Owner, id, request.MessageId, retry);
        var two = fixture.Runs.RetryRun(OtherOwner, other, request.MessageId, retry);
        Assert.NotEqual(one.RunId, two.RunId);
    }

    [Theory]
    [InlineData("timed_out", "timed_out", false)]
    [InlineData("incomplete", "incomplete_response", true)]
    [InlineData("interrupted", "interrupted", true)]
    [InlineData("failed", "provider_error", false)]
    public void PersistTerminalOutcomesAndPermitExplicitRetry(string state, string code, bool start)
    {
        using var fixture = new SqliteStorageFixture();
        var id = fixture.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        var run = fixture.Send(id);
        if (start) Assert.True(fixture.Transitions.TryStartRun(Owner, id, run.RunId));
        var finished = fixture.Transitions.FinishRun(Owner, id, run.RunId, state, code);
        Assert.Equal(finished, new SqliteRunStore(fixture.Reopen()).GetRun(Owner, id, run.RunId));
        SoloAiValidation.ValidateRun(finished, id);
        Assert.Equal("pending", fixture.Runs.RetryRun(Owner, id, run.UserMessageId, new(2, Guid.NewGuid())).State);
        Assert.Single(fixture.Chats.GetMessages(Owner, id).Messages);
    }

    [Fact]
    public void CancellationOccupiesActiveSlotAndRejectsLateCompletion()
    {
        using var fixture = new SqliteStorageFixture();
        var id = fixture.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        var run = fixture.Send(id);
        Assert.True(fixture.Transitions.TryStartRun(Owner, id, run.RunId));
        Assert.False(fixture.Transitions.TryStartRun(Owner, id, run.RunId));
        var cancelled = fixture.Transitions.RequestCancellation(Owner, id, run.RunId);
        Assert.Equal("cancellation_requested", cancelled.State);
        Assert.Equal(cancelled, fixture.Transitions.FinishRun(Owner, id, run.RunId, "completed", assistantText: "late"));
        AssertError("chat_busy", () => fixture.Send(id));
        fixture.Transitions.FinishRun(Owner, id, run.RunId, "cancelled", "cancelled");
        var retry = fixture.Runs.RetryRun(Owner, id, run.UserMessageId, new(2, Guid.NewGuid()));
        var completed = fixture.Complete(id, retry);
        Assert.Equal(completed, fixture.Transitions.RequestCancellation(Owner, id, retry.RunId));
        Assert.Equal(completed, fixture.Transitions.FinishRun(Owner, id, retry.RunId, "completed", assistantText: "duplicate"));
        Assert.Equal(2, fixture.Chats.GetMessages(Owner, id).Messages.Count);
    }

    [Fact]
    public async Task ConcurrentCancellationAndCompletionExposeOnlyTheWinningState()
    {
        using var fixture = new SqliteStorageFixture();
        var id = fixture.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId;
        var run = fixture.Send(id);
        fixture.Transitions.TryStartRun(Owner, id, run.RunId);
        var other = new SqliteRunTransitions(fixture.Reopen());
        var results = await Race(() => fixture.Transitions.RequestCancellation(Owner, id, run.RunId),
            () => other.FinishRun(Owner, id, run.RunId, "completed", assistantText: "winner"));
        Assert.Equal(results[0], results[1]);
        Assert.Contains(results[0].State, new[] { "completed", "cancellation_requested" });
        Assert.Equal(results[0].State == "completed" ? 2 : 1, fixture.Chats.GetMessages(Owner, id).Messages.Count);
    }
}

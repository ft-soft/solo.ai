using Solo.Ai.Client;
using Solo.Ai.Storage;
using Xunit;
using static Solo.Ai.Tests.SqliteStorageFixture;

namespace Solo.Ai.Tests;

public sealed class SoloAgentRunRuntimeTests
{
    [Fact]
    public async Task RecoverBeforeDispatchWithoutReplayingUnknownOutcomes()
    {
        await using var fixture = new GenerationFixture();
        var db = fixture.Storage;
        var unknown = db.Runs.SendMessage("unknown", db.Chats.CreateChat("unknown", Guid.NewGuid()).Chat.ChatId, new(2, Guid.NewGuid(), "unknown"));
        db.Transitions.TryStartRun("unknown", unknown.ChatId, unknown.RunId);
        var cancelled = db.Runs.SendMessage("cancelled", db.Chats.CreateChat("cancelled", Guid.NewGuid()).Chat.ChatId, new(2, Guid.NewGuid(), "cancelled"));
        db.Transitions.TryStartRun("cancelled", cancelled.ChatId, cancelled.RunId);
        db.Transitions.RequestCancellation("cancelled", cancelled.ChatId, cancelled.RunId);
        var expired = db.Runs.SendMessage("expired", db.Chats.CreateChat("expired", Guid.NewGuid()).Chat.ChatId, new(2, Guid.NewGuid(), "expired"));
        db.Execute("UPDATE GenerationRun SET DeadlineAt=CreatedAt+1 WHERE Id=$run;", ("$run", expired.RunId));
        var pending = db.Send(db.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId);
        await fixture.StartAsync();
        Assert.True(fixture.Runtime.IsReady);
        Assert.Equal("interrupted", db.Runs.GetRun("unknown", unknown.ChatId, unknown.RunId).State);
        Assert.Equal("cancelled", db.Runs.GetRun("cancelled", cancelled.ChatId, cancelled.RunId).State);
        Assert.Equal("timed_out", db.Runs.GetRun("expired", expired.ChatId, expired.RunId).State);
        Assert.Equal(pending.RunId, (await fixture.Model.NextAsync()).RequestId);
        fixture.Model.Complete(pending.RunId);
        await fixture.WaitForStateAsync(pending, "completed");
        await fixture.StopAsync();
        await fixture.StartAsync();
        Assert.Single(fixture.Model.Calls);
    }

    [Fact]
    public async Task BoundParallelismAcrossUsersAndKeepAcceptedWorkInDatabase()
    {
        await using var fixture = new GenerationFixture();
        var db = fixture.Storage;
        var first = db.Send(db.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId);
        var second = db.Runs.SendMessage(OtherOwner, db.Chats.CreateChat(OtherOwner, Guid.NewGuid()).Chat.ChatId, new(2, Guid.NewGuid(), "bob"));
        var third = db.Runs.SendMessage("third", db.Chats.CreateChat("third", Guid.NewGuid()).Chat.ChatId, new(2, Guid.NewGuid(), "third"));
        await fixture.StartAsync(new() { ["SoloAiGeneration:MaximumParallelRuns"] = "2" });
        var one = await fixture.Model.NextAsync();
        var two = await fixture.Model.NextAsync();
        Assert.Equal(new[] { first.RunId, second.RunId }.Order(), new[] { one.RequestId, two.RequestId }.Order());
        Assert.Equal("pending", db.Runs.GetRun("third", third.ChatId, third.RunId).State);
        Assert.DoesNotContain("bob", one.Messages.Select(message => message.Content).Where(_ => one.RequestId == first.RunId));
        fixture.Model.Complete(first.RunId);
        Assert.Equal(third.RunId, (await fixture.Model.NextAsync()).RequestId);
        fixture.Model.Complete(second.RunId);
        fixture.Model.Complete(third.RunId);
        await fixture.WaitForStateAsync(first, "completed");
        await fixture.WaitForStateAsync(second, "completed", OtherOwner);
        await fixture.WaitForStateAsync(third, "completed", "third");
    }

    [Fact]
    public async Task InterruptImmediatelyOnShutdownAndIgnoreLateProviderSuccess()
    {
        await using var fixture = new GenerationFixture();
        var db = fixture.Storage;
        var run = db.Send(db.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId);
        await fixture.StartAsync();
        await fixture.Model.NextAsync();
        await fixture.StopAsync();
        Assert.Equal("interrupted", db.Runs.GetRun(Owner, run.ChatId, run.RunId).State);
        fixture.Model.Complete(run.RunId);
        await fixture.StartAsync();
        Assert.Single(db.Chats.GetMessages(Owner, run.ChatId).Messages);
        Assert.Single(fixture.Model.Calls);
        var retry = db.Runs.RetryRun(Owner, run.ChatId, run.UserMessageId, new(2, Guid.NewGuid()));
        Assert.Equal(retry.RunId, (await fixture.Model.NextAsync()).RequestId);
        fixture.Model.Complete(retry.RunId);
        await fixture.WaitForStateAsync(retry, "completed");
    }

    [Theory]
    [InlineData("incomplete_response", "incomplete", "incomplete_response")]
    [InlineData("provider_error", "failed", "provider_error")]
    [InlineData("configuration_failure", "failed", "configuration_failure")]
    [InlineData("project_unavailable", "failed", "dependency_unavailable")]
    [InlineData("limit_exceeded", "failed", "limit_exceeded")]
    [InlineData("timeout", "timed_out", "timed_out")]
    public async Task PersistSafeOutcomesWithoutAssistantMessage(string outcome, string state, string code)
    {
        await using var fixture = new GenerationFixture();
        var db = fixture.Storage;
        var run = db.Send(db.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId);
        await fixture.StartAsync();
        await fixture.Model.NextAsync();
        fixture.Model.Fail(run.RunId, outcome);
        var result = await fixture.WaitForStateAsync(run, state);
        Assert.Equal(code, result.OutcomeCode);
        Assert.Null(result.AssistantMessageId);
        Assert.Single(db.Chats.GetMessages(Owner, run.ChatId).Messages);
        Assert.Single(fixture.Model.Calls);
    }

    [Fact]
    public async Task StopAdmissionAfterResultWriteFailureAndRecoverWithoutRedispatch()
    {
        await using var fixture = new GenerationFixture();
        var db = fixture.Storage;
        var run = db.Send(db.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId);
        db.Execute("CREATE TRIGGER FailResult BEFORE INSERT ON Message WHEN NEW.Role='assistant' BEGIN SELECT RAISE(ABORT, 'synthetic'); END;");
        await fixture.StartAsync();
        await fixture.Model.NextAsync();
        fixture.Model.Complete(run.RunId);
        await GenerationFixture.WaitAsync(() => !fixture.Runtime.IsReady);
        Assert.Equal(3, fixture.Logs.Entries.Count(entry => entry.Contains("Generation finalization failed")));
        Assert.DoesNotContain(fixture.Logs.Entries, entry => entry.Contains("hello") || entry.Contains("answer") || entry.Contains("synthetic-service-key"));
        Assert.Equal("running", db.Runs.GetRun(Owner, run.ChatId, run.RunId).State);
        Assert.Single(db.Chats.GetMessages(Owner, run.ChatId).Messages);
        var other = db.Chats.CreateChat(OtherOwner, Guid.NewGuid()).Chat.ChatId;
        AssertError("unavailable", () => db.Runs.SendMessage(OtherOwner, other, new(2, Guid.NewGuid(), "new"), accepting: fixture.Runtime.IsReady));
        Assert.Empty(db.Chats.GetMessages(OtherOwner, other).Messages);
        Assert.Equal(run.RunId, db.Runs.SendMessage(Owner, run.ChatId, new(2, run.UserMessageId, "hello"), accepting: false).RunId);
        db.Execute("DROP TRIGGER FailResult;");
        Assert.False(fixture.Runtime.IsReady);
        await fixture.StopAsync();
        await fixture.StartAsync();
        Assert.Equal("interrupted", db.Runs.GetRun(Owner, run.ChatId, run.RunId).State);
        Assert.Single(fixture.Model.Calls);
    }

    [Fact]
    public async Task RetryOnlyFinalizationWhenDatabaseBecomesWritableAgain()
    {
        await using var fixture = new GenerationFixture();
        var db = fixture.Storage;
        var run = db.Send(db.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId);
        db.Execute("CREATE TRIGGER FailResult BEFORE INSERT ON Message WHEN NEW.Role='assistant' BEGIN SELECT RAISE(ABORT, 'synthetic'); END;");
        await fixture.StartAsync();
        await fixture.Model.NextAsync();
        fixture.Logs.OnEntry = entry =>
        {
            if (entry.Contains("Generation finalization failed")) db.Execute("DROP TRIGGER FailResult;");
        };
        fixture.Model.Complete(run.RunId);
        await fixture.WaitForStateAsync(run, "completed");
        Assert.True(fixture.Runtime.IsReady);
        Assert.Single(fixture.Model.Calls);
        Assert.Equal(2, db.Chats.GetMessages(Owner, run.ChatId).Messages.Count);
    }

    [Fact]
    public async Task UseStoredDeadlineEvenIfProviderIgnoresCancellation()
    {
        await using var fixture = new GenerationFixture();
        var db = fixture.Storage;
        var run = db.Send(db.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId);
        db.Execute("UPDATE GenerationRun SET DeadlineAt=$deadline WHERE Id=$run;",
            ("$deadline", DateTimeOffset.UtcNow.AddSeconds(2).Ticks), ("$run", run.RunId));
        await fixture.StartAsync();
        await fixture.Model.NextAsync();
        await fixture.WaitForStateAsync(run, "timed_out");
        fixture.Model.Complete(run.RunId);
        Assert.Single(db.Chats.GetMessages(Owner, run.ChatId).Messages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelOrDeleteDuringFinalizationRetryDefeatsProviderSuccess(bool delete)
    {
        await using var fixture = new GenerationFixture();
        var db = fixture.Storage;
        var run = db.Send(db.Chats.CreateChat(Owner, Guid.NewGuid()).Chat.ChatId);
        db.Execute("CREATE TRIGGER FailResult BEFORE INSERT ON Message WHEN NEW.Role='assistant' BEGIN SELECT RAISE(ABORT, 'synthetic'); END;");
        await fixture.StartAsync();
        await fixture.Model.NextAsync();
        fixture.Logs.OnEntry = entry =>
        {
            if (!entry.Contains("Generation finalization failed")) return;
            if (delete) db.Chats.DeleteChat(Owner, run.ChatId);
            else db.Transitions.RequestCancellation(Owner, run.ChatId, run.RunId);
            db.Execute("DROP TRIGGER FailResult;");
        };
        fixture.Model.Complete(run.RunId);
        if (delete)
        {
            await GenerationFixture.WaitAsync(() => db.Chats.GetCurrentChat(Owner) is null);
            Assert.Equal(0L, db.Execute("SELECT COUNT(*) FROM Message;"));
        }
        else
        {
            await fixture.WaitForStateAsync(run, "cancelled");
            Assert.Single(db.Chats.GetMessages(Owner, run.ChatId).Messages);
        }
        Assert.Single(fixture.Model.Calls);
    }
}

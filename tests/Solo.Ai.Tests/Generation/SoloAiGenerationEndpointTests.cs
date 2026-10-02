using System.Net;
using Microsoft.AspNetCore.Http;
using Solo.Ai.Client;
using Xunit;
using static Solo.Ai.Tests.ButlerApiFixture;

namespace Solo.Ai.Tests;

public sealed class SoloAiGenerationEndpointTests
{
    [Fact]
    public async Task LosingAcknowledgementDoesNotCancelRunOrCaptureRequestContext()
    {
        var model = new GenerationModelClient();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var api = await StartAsync(modelClient: model, beforeResponse: async context =>
        {
            if (context.Request.Method == "POST" && context.Request.Path.Value!.EndsWith("/messages") && !accepted.Task.IsCompleted)
            {
                accepted.TrySetResult();
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
        });
        var chat = api.Storage.Chats.CreateChat(Alice, Guid.NewGuid()).Chat.ChatId;
        var send = new SendMessageRequest(2, Guid.NewGuid(), "request-secret-marker");
        using var disconnected = new CancellationTokenSource();
        var response = api.SendAsync("POST", $"/api/v2/chats/{chat}/messages", send, cancellationToken: disconnected.Token);
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var run = api.Storage.Chats.GetChat(Alice, chat).LatestRun!;
        disconnected.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response);
        Assert.Equal(run.RunId, (await model.NextAsync()).RequestId);
        Assert.All(model.CapturedHttpContext, Assert.False);
        model.Complete(run.RunId);
        await WaitForStateAsync(api, run, "completed");
        using var repeated = await api.SendAsync("POST", $"/api/v2/chats/{chat}/messages", send);
        Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
        var existing = await ReadDataAsync<SoloAiRun>(repeated);
        Assert.Equal(run.RunId, existing.RunId);
        Assert.Equal("completed", existing.State);
        Assert.Single(model.Calls);
        Assert.Equal(2, api.Storage.Chats.GetMessages(Alice, chat).Messages.Count);
    }

    [Fact]
    public async Task CancelThenRetryAndDeletePreventLateWritesAcrossNewChat()
    {
        var model = new GenerationModelClient { IgnoreCancellation = true };
        using var api = await StartAsync(modelClient: model);
        var chat = api.Storage.Chats.CreateChat(Alice, Guid.NewGuid()).Chat.ChatId;
        using var accepted = await api.SendAsync("POST", $"/api/v2/chats/{chat}/messages", new SendMessageRequest(2, Guid.NewGuid(), "first"));
        var original = await ReadDataAsync<SoloAiRun>(accepted);
        await model.NextAsync();
        using var cancel = await api.SendAsync("POST", $"/api/v2/chats/{chat}/runs/{original.RunId}/cancel");
        Assert.Contains((await ReadDataAsync<SoloAiRun>(cancel)).State, new[] { "cancellation_requested", "cancelled" });
        await WaitForStateAsync(api, original, "cancelled");
        var retryRequest = new RetryRunRequest(2, Guid.NewGuid());
        var retryPath = $"/api/v2/chats/{chat}/messages/{original.UserMessageId}/retry";
        using var retried = await api.SendAsync("POST", retryPath, retryRequest);
        var retry = await ReadDataAsync<SoloAiRun>(retried);
        Assert.NotEqual(original.RunId, retry.RunId);
        Assert.Equal(retry.RunId, (await model.NextAsync()).RequestId);
        model.Complete(original.RunId, "{\"text\":\"late-old\"}");
        using var repeated = await api.SendAsync("POST", retryPath, retryRequest);
        Assert.Equal(retry.RunId, (await ReadDataAsync<SoloAiRun>(repeated)).RunId);
        using var deleted = await api.SendAsync("DELETE", $"/api/v2/chats/{chat}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var fresh = api.Storage.Chats.CreateChat(Alice, Guid.NewGuid()).Chat.ChatId;
        model.Complete(retry.RunId, "{\"text\":\"late-retry\"}");
        using var oldDelete = await api.SendAsync("DELETE", $"/api/v2/chats/{chat}");
        Assert.Equal(HttpStatusCode.NotFound, oldDelete.StatusCode);
        using var oldRetry = await api.SendAsync("POST", retryPath, retryRequest);
        Assert.Equal(HttpStatusCode.NotFound, oldRetry.StatusCode);
        Assert.Empty(api.Storage.Chats.GetMessages(Alice, fresh).Messages);
        Assert.Equal(fresh, api.Storage.Chats.GetCurrentChat(Alice)!.ChatId);
        Assert.Equal(2, model.Calls.Count);
    }

    [Fact]
    public async Task CompleteBeforeCancelReturnsActualCompletedOutcome()
    {
        var model = new GenerationModelClient();
        using var api = await StartAsync(modelClient: model);
        var chat = api.Storage.Chats.CreateChat(Alice, Guid.NewGuid()).Chat.ChatId;
        using var response = await api.SendAsync("POST", $"/api/v2/chats/{chat}/messages", new SendMessageRequest(2, Guid.NewGuid(), "hello"));
        var run = await ReadDataAsync<SoloAiRun>(response);
        await model.NextAsync();
        model.Complete(run.RunId);
        await WaitForStateAsync(api, run, "completed");
        using var cancelled = await api.SendAsync("POST", $"/api/v2/chats/{chat}/runs/{run.RunId}/cancel");
        Assert.Equal("completed", (await ReadDataAsync<SoloAiRun>(cancelled)).State);
    }

    [Fact]
    public async Task CompetingSendsAndRetriesShareOneActiveRunAndPreserveOwnerIsolation()
    {
        var model = new GenerationModelClient();
        using var api = await StartAsync(modelClient: model);
        var alice = api.Storage.Chats.CreateChat(Alice, Guid.NewGuid()).Chat.ChatId;
        var bob = api.Storage.Chats.CreateChat(Bob, Guid.NewGuid()).Chat.ChatId;
        var path = $"/api/v2/chats/{alice}/messages";
        var responses = await Task.WhenAll(api.SendAsync("POST", path, new SendMessageRequest(2, Guid.NewGuid(), "one")),
            api.SendAsync("POST", path, new SendMessageRequest(2, Guid.NewGuid(), "two")));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Accepted);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        var run = await ReadDataAsync<SoloAiRun>(responses.Single(response => response.StatusCode == HttpStatusCode.Accepted));
        foreach (var response in responses) response.Dispose();
        using var other = await api.SendAsync("POST", $"/api/v2/chats/{bob}/messages", new SendMessageRequest(2, Guid.NewGuid(), "bob-only"), api.CreateToken(Bob));
        Assert.Equal(HttpStatusCode.Accepted, other.StatusCode);
        var otherRun = await ReadDataAsync<SoloAiRun>(other);
        await model.NextAsync();
        await model.NextAsync();
        Assert.DoesNotContain(model.Calls.Single(call => call.RequestId == run.RunId).Messages, message => message.Content == "bob-only");
        model.Fail(run.RunId, "provider_error");
        model.Complete(otherRun.RunId);
        await WaitForStateAsync(api, run, "failed");
        var retryPath = $"/api/v2/chats/{alice}/messages/{run.UserMessageId}/retry";
        var retries = await Task.WhenAll(api.SendAsync("POST", retryPath, new RetryRunRequest(2, Guid.NewGuid())),
            api.SendAsync("POST", retryPath, new RetryRunRequest(2, Guid.NewGuid())));
        Assert.Single(retries, response => response.StatusCode == HttpStatusCode.Accepted);
        Assert.Single(retries, response => response.StatusCode == HttpStatusCode.Conflict);
        foreach (var response in retries) response.Dispose();
        var request = await model.NextAsync();
        model.Complete(request.RequestId);
    }

    [Fact]
    public async Task ExposeFinalizationFailureThroughHealthAndRejectOnlyNewWork()
    {
        var model = new GenerationModelClient();
        using var api = await StartAsync(modelClient: model);
        using var ready = await api.Client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var chat = api.Storage.Chats.CreateChat(Alice, Guid.NewGuid()).Chat.ChatId;
        var send = new SendMessageRequest(2, Guid.NewGuid(), "hello");
        using var accepted = await api.SendAsync("POST", $"/api/v2/chats/{chat}/messages", send);
        var run = await ReadDataAsync<SoloAiRun>(accepted);
        await model.NextAsync();
        api.Storage.Execute("CREATE TRIGGER FailResult BEFORE INSERT ON Message WHEN NEW.Role='assistant' BEGIN SELECT RAISE(ABORT, 'private-db-error'); END;");
        model.Complete(run.RunId);
        await GenerationFixture.WaitAsync(() => !api.Runtime.IsReady);
        using var unhealthy = await api.Client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unhealthy.StatusCode);
        using var duplicate = await api.SendAsync("POST", $"/api/v2/chats/{chat}/messages", send);
        Assert.Equal(run.RunId, (await ReadDataAsync<SoloAiRun>(duplicate)).RunId);
        Assert.Equal("running", (await ReadDataAsync<SoloAiRun>(duplicate)).State);
        var bob = api.Storage.Chats.CreateChat(Bob, Guid.NewGuid()).Chat.ChatId;
        using var refused = await api.SendAsync("POST", $"/api/v2/chats/{bob}/messages", new SendMessageRequest(2, Guid.NewGuid(), "new"), api.CreateToken(Bob));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Empty(api.Storage.Chats.GetMessages(Bob, bob).Messages);
        Assert.Single(model.Calls);
    }

    [Fact]
    public async Task FullQueueRejectsNewSendButAllowsDedupAndPendingCancellation()
    {
        var model = new GenerationModelClient { IgnoreCancellation = true };
        using var api = await StartAsync(modelClient: model, changes: new()
        {
            ["SoloAiGeneration:MaximumParallelRuns"] = "1", ["SoloAiGeneration:MaximumPendingRuns"] = "1",
        });
        var alice = api.Storage.Chats.CreateChat(Alice, Guid.NewGuid()).Chat.ChatId;
        using var first = await api.SendAsync("POST", $"/api/v2/chats/{alice}/messages", new SendMessageRequest(2, Guid.NewGuid(), "running"));
        var running = await ReadDataAsync<SoloAiRun>(first);
        await model.NextAsync();
        var bob = api.Storage.Chats.CreateChat(Bob, Guid.NewGuid()).Chat.ChatId;
        var send = new SendMessageRequest(2, Guid.NewGuid(), "pending");
        var path = $"/api/v2/chats/{bob}/messages";
        using var second = await api.SendAsync("POST", path, send, api.CreateToken(Bob));
        var pending = await ReadDataAsync<SoloAiRun>(second);
        Assert.Equal("pending", pending.State);
        using var duplicate = await api.SendAsync("POST", path, send, api.CreateToken(Bob));
        Assert.Equal(pending.RunId, (await ReadDataAsync<SoloAiRun>(duplicate)).RunId);
        const string thirdOwner = "00000000-0000-0000-0000-000000000003";
        var third = api.Storage.Chats.CreateChat(thirdOwner, Guid.NewGuid()).Chat.ChatId;
        using var full = await api.SendAsync("POST", $"/api/v2/chats/{third}/messages", new SendMessageRequest(2, Guid.NewGuid(), "overflow"), api.CreateToken(thirdOwner));
        Assert.Equal(HttpStatusCode.TooManyRequests, full.StatusCode);
        Assert.Empty(api.Storage.Chats.GetMessages(thirdOwner, third).Messages);
        using var cancel = await api.SendAsync("POST", $"/api/v2/chats/{bob}/runs/{pending.RunId}/cancel", token: api.CreateToken(Bob));
        await GenerationFixture.WaitAsync(() => api.Storage.Runs.GetRun(Bob, bob, pending.RunId).State == "cancelled");
        Assert.Single(model.Calls);
        model.Complete(running.RunId);
        await WaitForStateAsync(api, running, "completed");
    }

    private static Task WaitForStateAsync(ButlerApiFixture api, SoloAiRun run, string state) =>
        GenerationFixture.WaitAsync(() => api.Storage.Runs.GetRun(Alice, run.ChatId, run.RunId).State == state);
}

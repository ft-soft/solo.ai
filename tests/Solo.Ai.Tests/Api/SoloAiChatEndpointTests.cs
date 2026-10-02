using System.Net;
using Solo.Ai.Client;
using Xunit;
using static Solo.Ai.Tests.ButlerApiFixture;

namespace Solo.Ai.Tests;

public sealed class SoloAiChatEndpointTests
{
    [Theory]
    [InlineData("POST", "")]
    [InlineData("GET", "/current")]
    [InlineData("GET", "/{chat}")]
    [InlineData("GET", "/{chat}/messages")]
    [InlineData("PATCH", "/{chat}")]
    [InlineData("DELETE", "/{chat}")]
    [InlineData("POST", "/{chat}/messages")]
    [InlineData("GET", "/{chat}/runs/{run}")]
    [InlineData("POST", "/{chat}/runs/{run}/cancel")]
    [InlineData("POST", "/{chat}/messages/{message}/retry")]
    public async Task ApplySamePolicyToEveryRouteBeforeBodyValidation(string method, string suffix)
    {
        using var api = await StartAsync();
        var path = "/api/v2/chats" + suffix.Replace("{chat}", Guid.NewGuid().ToString()).Replace("{run}", Guid.NewGuid().ToString()).Replace("{message}", Guid.NewGuid().ToString());
        foreach (var token in new[] { "", "malformed", api.CreateToken(change: descriptor => descriptor.Claims["client_id"] = "other") })
        {
            using var response = await api.SendAsync(method, path, "{", token,
                request => request.Headers.Add("X-Solo-User-Id", Alice));
            Assert.Equal(token is "" or "malformed" ? 401 : 403, (int)response.StatusCode);
        }
        Assert.Null(api.Storage.Chats.GetCurrentChat(Alice));
    }

    [Fact]
    public async Task PersistCrudAndIgnoreSpoofedHeader()
    {
        using var api = await StartAsync();
        var id = Guid.NewGuid();
        using var empty = await api.SendAsync("GET", "/api/v2/chats/current");
        Assert.Equal(HttpStatusCode.NoContent, empty.StatusCode);
        using var created = await api.SendAsync("POST", "/api/v2/chats", new CreateChatRequest(2, id), change:
            request => request.Headers.Add("X-Solo-User-Id", Bob));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(id, (await ReadDataAsync<SoloAiChat>(created)).ChatId);
        Assert.Null(api.Storage.Chats.GetCurrentChat(Bob));
        using var again = await api.SendAsync("POST", "/api/v2/chats", new CreateChatRequest(2, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(id, (await ReadDataAsync<SoloAiChat>(again)).ChatId);
        using var renamed = await api.SendAsync("PATCH", $"/api/v2/chats/{id}", new RenameChatRequest(2, "Мой чат"));
        Assert.Equal("Мой чат", (await ReadDataAsync<SoloAiChat>(renamed)).Title);
        using var current = await api.SendAsync("GET", "/api/v2/chats/current", change:
            request => request.Headers.Add("X-Solo-User-Id", Bob));
        Assert.Equal("Мой чат", (await ReadDataAsync<SoloAiChat>(current)).Title);
        Assert.Equal(id, new Solo.Ai.Storage.SqliteChatStore(api.Storage.Reopen()).GetCurrentChat(Alice)!.ChatId);
        using var removed = await api.SendAsync("DELETE", $"/api/v2/chats/{id}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Empty(await removed.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        using var repeated = await api.SendAsync("DELETE", $"/api/v2/chats/{id}");
        Assert.Equal(HttpStatusCode.NotFound, repeated.StatusCode);
    }

    [Fact]
    public async Task HideForeignAndMissingResourcesAcrossAllRoutesAndChildIds()
    {
        using var api = await StartAsync();
        var own = api.Storage.Chats.CreateChat(Alice, Guid.NewGuid()).Chat;
        var foreign = api.Storage.Chats.CreateChat(Bob, Guid.NewGuid()).Chat;
        var run = api.Storage.Runs.SendMessage(Bob, foreign.ChatId, new(2, Guid.NewGuid(), "private-marker"));
        foreach (var chat in new[] { foreign.ChatId, Guid.NewGuid() })
        {
            var path = $"/api/v2/chats/{chat}";
            var requests = new (string Method, string Path, object? Body)[]
            {
                ("GET", path, null), ("PATCH", path, new RenameChatRequest(2, "stolen")), ("DELETE", path, null),
                ("GET", path + "/messages", null), ("POST", path + "/messages", new SendMessageRequest(2, Guid.NewGuid(), "hello")),
                ("GET", path + $"/runs/{run.RunId}", null), ("POST", path + $"/runs/{run.RunId}/cancel", null),
                ("POST", path + $"/messages/{run.UserMessageId}/retry", new RetryRunRequest(2, Guid.NewGuid())),
            };
            foreach (var request in requests)
            {
                using var response = await api.SendAsync(request.Method, request.Path, request.Body);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.DoesNotContain("private-marker", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            }
        }
        using var stolenCreate = await api.SendAsync("POST", "/api/v2/chats", new CreateChatRequest(2, foreign.ChatId));
        Assert.Equal(HttpStatusCode.NotFound, stolenCreate.StatusCode);
        foreach (var child in new[] { $"runs/{run.RunId}", $"runs/{run.RunId}/cancel", $"messages/{run.UserMessageId}/retry" })
        {
            using var response = await api.SendAsync(child.EndsWith(run.RunId.ToString()) ? "GET" : "POST",
                $"/api/v2/chats/{own.ChatId}/{child}", child.EndsWith("retry") ? new RetryRunRequest(2, Guid.NewGuid()) : null);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var current = await api.SendAsync("GET", "/api/v2/chats/current", change: request => request.Headers.Add("X-Solo-User-Id", Bob));
        Assert.Equal(own.ChatId, (await ReadDataAsync<SoloAiChat>(current)).ChatId);
        Assert.Equal("pending", api.Storage.Runs.GetRun(Bob, foreign.ChatId, run.RunId).State);
    }

    [Fact]
    public async Task ReadAndCancelSeededRunButNeverAcceptSendOrRetryWithoutWorker()
    {
        using var api = await StartAsync();
        var chat = api.Storage.Chats.CreateChat(Alice, Guid.NewGuid()).Chat;
        var path = $"/api/v2/chats/{chat.ChatId}";
        using var send = await api.SendAsync("POST", path + "/messages", new SendMessageRequest(2, Guid.NewGuid(), "hello"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, send.StatusCode);
        Assert.Empty(api.Storage.Chats.GetMessages(Alice, chat.ChatId).Messages);
        var run = api.Storage.Runs.SendMessage(Alice, chat.ChatId, new(2, Guid.NewGuid(), "seed"));
        using var status = await api.SendAsync("GET", path + $"/runs/{run.RunId}");
        Assert.Equal(run, await ReadDataAsync<SoloAiRun>(status));
        using var messages = await api.SendAsync("GET", path + "/messages");
        Assert.Single((await ReadDataAsync<SoloAiMessagePage>(messages)).Messages);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var cancel = await api.SendAsync("POST", path + $"/runs/{run.RunId}/cancel");
            Assert.Equal("cancellation_requested", (await ReadDataAsync<SoloAiRun>(cancel)).State);
        }
        api.Storage.Transitions.FinishRun(Alice, chat.ChatId, run.RunId, "cancelled", "cancelled");
        using var retry = await api.SendAsync("POST", path + $"/messages/{run.UserMessageId}/retry", new RetryRunRequest(2, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, retry.StatusCode);
        Assert.Equal(run.RunId, api.Storage.Chats.GetChat(Alice, chat.ChatId).LatestRun!.RunId);
        using var old = await api.SendAsync("POST", "/api/v1/solo-agent/generations", "{}");
        Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
    }
}

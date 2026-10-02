using System.Net;
using System.Text;
using Solo.Ai.Client;
using Xunit;
using static Solo.Ai.Tests.ButlerApiFixture;

namespace Solo.Ai.Tests;

public sealed class SoloAiHttpBoundaryTests
{
    [Theory]
    [InlineData("null", "validation_failed")]
    [InlineData("{}", "validation_failed")]
    [InlineData("{", "validation_failed")]
    [InlineData("{\"contractVersion\":2,\"chatId\":\"00000000-0000-0000-0000-000000000003\",\"owner\":\"other\"}", "validation_failed")]
    [InlineData("{\"contractVersion\":2,\"chatId\":\"00000000-0000-0000-0000-000000000003\",\"userId\":\"other\"}", "validation_failed")]
    [InlineData("{\"contractVersion\":2,\"contractVersion\":2,\"chatId\":\"00000000-0000-0000-0000-000000000003\"}", "validation_failed")]
    [InlineData("{\"contractVersion\":1,\"chatId\":\"00000000-0000-0000-0000-000000000003\"}", "unsupported_contract")]
    public async Task RejectInvalidBodiesWithoutWriting(string body, string code)
    {
        using var api = await StartAsync();
        using var response = await api.SendAsync("POST", "/api/v2/chats", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(code, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Null(api.Storage.Chats.GetCurrentChat(Alice));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("request-id")]
    [InlineData("duplicate-version")]
    [InlineData("duplicate-request-id")]
    [InlineData("content-type")]
    public async Task RejectInvalidHeadersBeforeWriting(string fault)
    {
        using var api = await StartAsync();
        using var response = await api.SendAsync("POST", "/api/v2/chats", new CreateChatRequest(2, Guid.NewGuid()), change: request =>
        {
            if (fault == "version") request.Headers.Remove(SoloAiProtocol.VersionHeader);
            if (fault == "request-id") request.Headers.Remove(SoloAiProtocol.RequestIdHeader);
            if (fault == "duplicate-version") request.Headers.Add(SoloAiProtocol.VersionHeader, "2");
            if (fault == "duplicate-request-id") request.Headers.Add(SoloAiProtocol.RequestIdHeader, Guid.NewGuid().ToString());
            if (fault == "content-type") request.Content!.Headers.ContentType = new("text/plain");
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(api.Storage.Chats.GetCurrentChat(Alice));
    }

    [Fact]
    public async Task RejectOversizedDeclaredAndChunkedBodiesBeforeWriting()
    {
        using var api = await StartAsync(new() { ["SoloAiApi:MaxRequestBytes"] = "100" });
        foreach (var chunked in new[] { false, true })
        {
            using var response = await api.SendAsync("POST", "/api/v2/chats", new string(' ', 101), change: request =>
            {
                if (chunked)
                {
                    request.Content = new ChunkedContent(new string(' ', 101));
                    request.Content.Headers.ContentType = new("application/json");
                }
            });
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
        Assert.Null(api.Storage.Chats.GetCurrentChat(Alice));
    }

    [Fact]
    public async Task KeepPaginationWithinResponseCapWithoutLosingMessages()
    {
        using var api = await StartAsync(new() { ["SoloAiApi:MaxResponseBytes"] = "16384" });
        var chat = api.Storage.Chats.CreateChat(Alice, Guid.NewGuid()).Chat;
        for (var index = 0; index < 4; index++)
        {
            var run = api.Storage.Runs.SendMessage(Alice, chat.ChatId, new(2, Guid.NewGuid(), new string('x', 6000)));
            api.Storage.Transitions.FinishRun(Alice, chat.ChatId, run.RunId, "failed", "provider_error");
        }
        var seen = new HashSet<Guid>();
        string? cursor = null;
        do
        {
            using var response = await api.SendAsync("GET", $"/api/v2/chats/{chat.ChatId}/messages" +
                (cursor is null ? "" : "?cursor=" + Uri.EscapeDataString(cursor)));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True((await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length <= 16384);
            var page = await ReadDataAsync<SoloAiMessagePage>(response);
            SoloAiValidation.ValidatePage(page, chat.ChatId, cursor, 50);
            Assert.All(page.Messages, message => Assert.True(seen.Add(message.MessageId)));
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(4, seen.Count);
        var oversized = api.Storage.Runs.SendMessage(Alice, chat.ChatId, new(2, Guid.NewGuid(), new string('x', 20000)));
        using var rejected = await api.SendAsync("GET", $"/api/v2/chats/{chat.ChatId}/messages?limit=1");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
        Assert.Equal(oversized.RunId, api.Storage.Chats.GetChat(Alice, chat.ChatId).LatestRun!.RunId);
    }

    [Fact]
    public async Task RejectUnavailableStorageAndHideDatabaseErrors()
    {
        using var disabled = await StartAsync(new() { ["SoloAiStorage:Enabled"] = "false" });
        using var unavailable = await disabled.SendAsync("GET", "/api/v2/chats/current");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        using var api = await StartAsync();
        api.Storage.Execute("CREATE TRIGGER FailCreate BEFORE INSERT ON Chat BEGIN SELECT RAISE(ABORT, 'private-database-marker'); END;");
        using var response = await api.SendAsync("POST", "/api/v2/chats", new CreateChatRequest(2, Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("private-database-marker", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PreserveWireHeadersAndRejectUnexpectedBodyOnBodylessRoute()
    {
        using var api = await StartAsync();
        var requestId = Guid.NewGuid().ToString();
        using var response = await api.SendAsync("GET", "/api/v2/chats/current", change: request =>
        {
            request.Headers.Remove(SoloAiProtocol.RequestIdHeader);
            request.Headers.Add(SoloAiProtocol.RequestIdHeader, requestId);
        });
        Assert.Equal(requestId, Assert.Single(response.Headers.GetValues(SoloAiProtocol.RequestIdHeader)));
        Assert.Equal("2", Assert.Single(response.Headers.GetValues(SoloAiProtocol.VersionHeader)));
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var injected = await api.SendAsync("GET", "/api/v2/chats/current", "{\"owner\":\"other\"}");
        Assert.Equal(HttpStatusCode.BadRequest, injected.StatusCode);
    }

    private sealed class ChunkedContent(string text) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();
    }
}

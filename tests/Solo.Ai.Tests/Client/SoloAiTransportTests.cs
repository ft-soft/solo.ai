using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Solo.Ai.Client;
using Xunit;
using static Solo.Ai.Tests.SoloAiHttpFixture;

namespace Solo.Ai.Tests;

public sealed class SoloAiTransportTests
{
    [Theory]
    [InlineData("text/html", "utf-8")]
    [InlineData("application/json", "utf-16")]
    public async Task RejectUnexpectedResponseEncoding(string mediaType, string charset)
    {
        using var http = new SoloAiHttpFixture((_, _) =>
        {
            var reply = Reply("run-v2.json", 202);
            reply.Content.Headers.ContentType = new(mediaType) { CharSet = charset };
            return Task.FromResult(reply);
        });
        var error = await Assert.ThrowsAsync<SoloAiExchangeException>(() => http.CreateSdk()
            .SendMessageAsync(ChatId, MessageId, "text", TestContext.Current.CancellationToken));
        Assert.Equal("invalid_response", error.Code);
        Assert.True(error.OutcomeUnknown);
    }

    [Fact]
    public async Task RejectDifferentCreatedChatAndRetriedRunInOriginalSendAcknowledgement()
    {
        using var create = new SoloAiHttpFixture((_, _) => Task.FromResult(Reply("chat-v2.json", 201)));
        var error = await Assert.ThrowsAsync<SoloAiExchangeException>(() => create.CreateSdk().CreateChatAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.Equal("invalid_response", error.Code);
        Assert.True(error.OutcomeUnknown);
        using var send = new SoloAiHttpFixture((_, _) => Task.FromResult(Reply("retry-v2.json", 202)));
        Assert.Equal("invalid_response", (await Assert.ThrowsAsync<SoloAiExchangeException>(() =>
            send.CreateSdk().SendMessageAsync(ChatId, MessageId, "text", TestContext.Current.CancellationToken))).Code);
    }

    [Fact]
    public async Task RejectAlreadyCancelledObservationBeforeSending()
    {
        using var http = new SoloAiHttpFixture((_, _) => throw new InvalidOperationException("Must not send"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => http.CreateSdk().SendMessageAsync(ChatId, MessageId, "text", cancellation.Token));
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task RejectStoredMalformedAndForeignRepliesAsUnknownSendOutcomes()
    {
        var bodies = JsonNode.Parse(ReadFixture("malformed-v2.json"))!.AsArray().Select(item => item!["body"]!.GetValue<string>())
            .Append(ReadFixture("foreign-correlation-v2.json"));
        foreach (var body in bodies)
        {
            using var http = new SoloAiHttpFixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
            var error = await Assert.ThrowsAsync<SoloAiExchangeException>(() => http.CreateSdk().SendMessageAsync(ChatId, MessageId, "text", TestContext.Current.CancellationToken));
            Assert.Contains(error.Code, new[] { "invalid_response", "unsupported_contract" });
            Assert.True(error.OutcomeUnknown);
            Assert.Equal(1, http.Calls);
        }
    }

    [Theory]
    [InlineData("chatId")]
    [InlineData("runId")]
    [InlineData("userMessageId")]
    [InlineData("retryId")]
    public async Task RejectForeignResourceCorrelations(string field)
    {
        var body = JsonNode.Parse(ReadFixture("retry-v2.json"))!;
        body["data"]![field] = Guid.NewGuid();
        using var http = new SoloAiHttpFixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
            { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") }));
        // Retry creates a server run ID, while polling checks that ID independently.
        if (field == "runId")
        {
            using var poll = new SoloAiHttpFixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") }));
            Assert.Equal("invalid_response", (await Assert.ThrowsAsync<SoloAiExchangeException>(() => poll.CreateSdk().GetRunAsync(ChatId, RunId, TestContext.Current.CancellationToken))).Code);
        }
        else
        {
            var error = await Assert.ThrowsAsync<SoloAiExchangeException>(() => http.CreateSdk().RetryRunAsync(ChatId, MessageId, RetryId, TestContext.Current.CancellationToken));
            Assert.Equal("invalid_response", error.Code);
            Assert.True(error.OutcomeUnknown);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("duplicate")]
    [InlineData("version")]
    public async Task ValidateHeadersEvenOnEmptyResponses(string scenario)
    {
        using var http = new SoloAiHttpFixture((request, _) =>
        {
            var reply = Reply(null, 204);
            if (scenario != "missing")
            {
                reply.Headers.Add(SoloAiProtocol.VersionHeader, scenario == "version" ? "1" : "2");
                reply.Headers.Add(SoloAiProtocol.RequestIdHeader, scenario == "foreign" ? Guid.NewGuid().ToString() : request.Headers.GetValues(SoloAiProtocol.RequestIdHeader).Single());
                if (scenario == "duplicate") reply.Headers.Add(SoloAiProtocol.RequestIdHeader, Guid.NewGuid().ToString());
            }
            return Task.FromResult(reply);
        }) { EchoHeaders = false };
        var error = await Assert.ThrowsAsync<SoloAiExchangeException>(() => http.CreateSdk().DeleteChatAsync(ChatId, TestContext.Current.CancellationToken));
        Assert.True(error.OutcomeUnknown);
    }

    [Theory]
    [InlineData(200, "run-v2.json")]
    [InlineData(204, null)]
    [InlineData(302, "run-v2.json")]
    [InlineData(500, "run-v2.json")]
    [InlineData(409, "run-v2.json")]
    public async Task RejectUnexpectedStatusWithoutRetry(int status, string? fixture)
    {
        using var http = new SoloAiHttpFixture((_, _) => Task.FromResult(Reply(fixture, status)));
        var error = await Assert.ThrowsAsync<SoloAiExchangeException>(() => http.CreateSdk().SendMessageAsync(ChatId, MessageId, "text", TestContext.Current.CancellationToken));
        Assert.Equal("invalid_response", error.Code);
        Assert.True(error.OutcomeUnknown);
        Assert.Equal(1, http.Calls);
    }

    [Theory]
    [InlineData(401, "authentication_required")]
    [InlineData(403, "forbidden_actor")]
    public async Task AcceptBodylessAuthenticationRejection(int status, string code)
    {
        using var http = new SoloAiHttpFixture((_, _) => Task.FromResult(Reply(null, status))) { EchoHeaders = false };
        var error = await Assert.ThrowsAsync<SoloAiExchangeException>(() => http.CreateSdk().SendMessageAsync(ChatId, MessageId, "text", TestContext.Current.CancellationToken));
        Assert.Equal(code, error.Code);
        Assert.False(error.OutcomeUnknown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundCompleteResponseWithAndWithoutContentLength(bool chunked)
    {
        using var http = new SoloAiHttpFixture((_, _) =>
        {
            var response = Reply("run-v2.json", 202);
            if (chunked)
            {
                response.Content = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(ReadFixture("run-v2.json"))));
                response.Content.Headers.ContentType = new("application/json");
            }
            return Task.FromResult(response);
        });
        var error = await Assert.ThrowsAsync<SoloAiExchangeException>(() => http.CreateSdk(responseLimit: 32).SendMessageAsync(ChatId, MessageId, "text", TestContext.Current.CancellationToken));
        Assert.Equal("limit_exceeded", error.Code);
        Assert.True(error.OutcomeUnknown);
    }

    [Fact]
    public async Task RejectOversizedSerializedRequestBeforeDispatch()
    {
        using var http = new SoloAiHttpFixture((_, _) => throw new InvalidOperationException("Must not send"));
        var error = await Assert.ThrowsAsync<SoloAiExchangeException>(() => http.CreateSdk(requestLimit: 10).SendMessageAsync(ChatId, MessageId, "text", TestContext.Current.CancellationToken));
        Assert.Equal("limit_exceeded", error.Code);
        Assert.False(error.OutcomeUnknown);
        Assert.Equal(0, http.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DistinguishReadFailureFromUnknownCommandOutcome(bool command)
    {
        using var http = new SoloAiHttpFixture((_, _) => throw new HttpRequestException("PRIVATE-TOKEN-TEXT"));
        var sdk = http.CreateSdk();
        var error = await Assert.ThrowsAsync<SoloAiExchangeException>(() => command
            ? sdk.SendMessageAsync(ChatId, MessageId, "text", TestContext.Current.CancellationToken) : (Task)sdk.GetCurrentChatAsync(TestContext.Current.CancellationToken));
        Assert.Equal("transport_error", error.Code);
        Assert.Equal(command, error.OutcomeUnknown);
        Assert.DoesNotContain("PRIVATE-TOKEN-TEXT", error.ToString());
        Assert.Null(error.InnerException);
        Assert.Equal(1, http.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelObservationWithoutSendingRunCancellation(bool cancelByCaller)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new SoloAiHttpFixture(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Reply("run-v2.json", 202);
        });
        using var cancellation = new CancellationTokenSource();
        var pending = http.CreateSdk(timeout: cancelByCaller ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(50))
            .SendMessageAsync(ChatId, MessageId, "text", cancellation.Token);
        await entered.Task;
        if (cancelByCaller)
        {
            cancellation.Cancel();
            var error = await Assert.ThrowsAsync<SoloAiRequestCanceledException>(() => pending);
            Assert.True(error.OutcomeUnknown);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        else
        {
            var error = await Assert.ThrowsAsync<SoloAiExchangeException>(() => pending);
            Assert.Equal("http_timeout", error.Code);
            Assert.True(error.OutcomeUnknown);
        }
        Assert.Equal(1, http.Calls);
    }

    [Fact]
    public async Task BoundBodyReadingByDeadline()
    {
        using var http = new SoloAiHttpFixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StreamContent(new StalledStream()),
        }));
        var error = await Assert.ThrowsAsync<SoloAiExchangeException>(() => http.CreateSdk(timeout: TimeSpan.FromMilliseconds(50))
            .SendMessageAsync(ChatId, MessageId, "text", TestContext.Current.CancellationToken));
        Assert.Equal("http_timeout", error.Code);
        Assert.True(error.OutcomeUnknown);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class StalledStream() : MemoryStream
    {
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}

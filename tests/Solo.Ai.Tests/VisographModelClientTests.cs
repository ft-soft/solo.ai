using System.Net;
using System.Text;
using System.Text.Json;
using Solo.Ai.Visograph;
using Xunit;

namespace Solo.Ai.Tests;

public sealed class VisographModelClientTests
{
    [Fact]
    public async Task RoundTripSharedFixtureOnceWithoutCredentialsInModelMessages()
    {
        var request = ReadFixture();
        using var handler = new ResponseHandler(HttpStatusCode.OK, JsonSerializer.Serialize(
            new ModelGenerationResponse(1, request.RequestId, "completed") { Content = "{}" }, ModelProtocol.JsonOptions));
        using var client = CreateClient(handler);

        var result = await client.GenerateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("completed", result.Outcome);
        Assert.Null(result.Usage);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("SYNTHETIC-SERVICE-KEY", handler.Body);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        var parsed = ModelProtocol.ParseRequest(Encoding.UTF8.GetBytes(handler.Body));
        Assert.Equal(request.Messages, parsed.Messages);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "unsupported_contract")]
    [InlineData(HttpStatusCode.MethodNotAllowed, "unsupported_contract")]
    [InlineData(HttpStatusCode.Unauthorized, "authentication_failed")]
    public async Task RejectOlderOrUnauthenticatedServerWithoutFallback(HttpStatusCode status, string outcome)
    {
        using var handler = new ResponseHandler(status, "PRIVATE-PROVIDER-ERROR");
        using var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<ModelExchangeException>(() => client.GenerateAsync(ReadFixture(), TestContext.Current.CancellationToken));

        Assert.Equal(outcome, error.Code);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("PRIVATE-PROVIDER-ERROR", error.ToString());
    }

    [Fact]
    public async Task PreserveEarlyLimitFailureWithUnknownCorrelation()
    {
        using var handler = new ResponseHandler(HttpStatusCode.RequestEntityTooLarge, JsonSerializer.Serialize(
            new ModelGenerationResponse(1, null, "limit_exceeded") { Stage = "budget" }, ModelProtocol.JsonOptions));
        using var client = CreateClient(handler);

        Assert.Equal("limit_exceeded", (await client.GenerateAsync(ReadFixture(), TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task RejectBodyAndResponseLimitsWithoutRetry()
    {
        using var handler = new ResponseHandler(HttpStatusCode.OK, new string('x', 2000));
        using var client = CreateClient(handler, maximumResponseBytes: 10);

        var error = await Assert.ThrowsAsync<ModelExchangeException>(() => client.GenerateAsync(ReadFixture(), TestContext.Current.CancellationToken));

        Assert.Equal("limit_exceeded", error.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void RejectToolVersionWhileFixturePreservesCallOrderAndCorrelation()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "future-tools-v2.json"));
        using var document = JsonDocument.Parse(bytes);
        var messages = document.RootElement.GetProperty("messages");
        Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
        Assert.Equal(messages[2].GetProperty("toolCalls")[0].GetProperty("callId").GetString(), messages[3].GetProperty("callId").GetString());
        Assert.Equal("unsupported_contract", Assert.Throws<ModelExchangeException>(() => ModelProtocol.ParseRequest(bytes)).Code);
    }

    [Fact]
    public async Task ConsumeAllSharedSuccessAndErrorFixturesWithoutFallback()
    {
        using var fixtures = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "responses-v1.json"), TestContext.Current.CancellationToken));
        foreach (var entry in fixtures.RootElement.EnumerateArray())
        {
            using var handler = new ResponseHandler((HttpStatusCode)entry.GetProperty("status").GetInt32(), entry.GetProperty("response").GetRawText());
            using var client = CreateClient(handler);
            var result = await client.GenerateAsync(ReadFixture(), TestContext.Current.CancellationToken);
            Assert.Equal(entry.GetProperty("response").GetProperty("outcome").GetString(), result.Outcome);
            Assert.Equal(1, handler.Calls);
            Assert.Null(result.Usage);
        }
    }

    private static ModelGenerationRequest ReadFixture() => ModelProtocol.ParseRequest(
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "request-v1.json")));

    [Fact]
    public async Task DeadlineIncludesReadingResponseBodyAndNeverRetries()
    {
        using var handler = new StalledBodyHandler();
        using var client = new VisographModelClient(new()
        {
            Enabled = true, BaseUri = new("https://visograph.test/"), ApiKey = "synthetic-key",
            RequestTimeout = TimeSpan.FromMilliseconds(50), MaxRequestBytes = 100_000, MaxResponseBytes = 100_000,
        }, handler);
        var error = await Assert.ThrowsAsync<ModelExchangeException>(() => client.GenerateAsync(ReadFixture(), TestContext.Current.CancellationToken));
        Assert.Equal("timeout", error.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CallerCancellationWhileReadingIsPreserved()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var handler = new StalledBodyHandler();
        using var client = CreateClient(handler);
        var pending = client.GenerateAsync(ReadFixture(), cancellation.Token);
        await handler.Reading.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, handler.Calls);
    }

    private static VisographModelClient CreateClient(HttpMessageHandler handler, long maximumResponseBytes = 100_000) => new(new()
    {
        Enabled = true, BaseUri = new("https://visograph.test/"), ApiKey = "SYNTHETIC-SERVICE-KEY",
        RequestTimeout = TimeSpan.FromSeconds(5), MaxResponseBytes = maximumResponseBytes, MaxRequestBytes = 100_000,
    }, handler);

    private sealed class ResponseHandler(HttpStatusCode status, string content) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string Body { get; private set; } = string.Empty;
        public string? AuthorizationScheme { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            return new(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StalledBodyHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream(Reading)) });
        }
        private sealed class StalledStream(TaskCompletionSource reading) : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                reading.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}

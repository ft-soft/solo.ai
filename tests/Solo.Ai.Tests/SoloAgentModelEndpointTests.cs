using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solo.Ai.Api;
using Solo.Ai.Client;
using Solo.Ai.Visograph;
using Xunit;

namespace Solo.Ai.Tests;

public sealed class SoloAgentModelEndpointTests
{
    [Fact]
    public async Task ReceiveIndependentFixtureAndReturnCompatibleReplyAfterRuntimeAuthorization()
    {
        var model = new ModelClient();
        var gate = new RunAuthorizer();
        using var host = await CreateHostAsync(model, gate);
        using var client = CreateClient(host);
        using var response = await client.PostAsync(SoloAgentModelEndpoint.Path, CreateContent(ReadFixture()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reply = JsonSerializer.Deserialize<SoloAgentGenerationReply>(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), ModelProtocol.JsonOptions)!;
        var expected = JsonSerializer.Deserialize<SoloAgentGenerationReply>(await File.ReadAllTextAsync(FixturePath("reply-v1.json"), TestContext.Current.CancellationToken), ModelProtocol.JsonOptions)!;
        Assert.Equal(JsonSerializer.Serialize(expected, ModelProtocol.JsonOptions), JsonSerializer.Serialize(reply, ModelProtocol.JsonOptions));
        Assert.Equal(1, model.Calls);
        Assert.Equal(1, gate.Calls);
        Assert.Equal(new[] { "system", "user", "assistant", "user", "user" }, model.Request!.Messages.Select(message => message.Role));
        using var catalog = JsonDocument.Parse(model.Request.Messages[^2].Content);
        Assert.Equal(2, catalog.RootElement.GetProperty("options").GetArrayLength());
    }

    [Theory]
    [InlineData("unauthenticated", HttpStatusCode.Unauthorized)]
    [InlineData("foreign_run", HttpStatusCode.Forbidden)]
    [InlineData("duplicate", HttpStatusCode.Forbidden)]
    [InlineData("missing_runtime", HttpStatusCode.ServiceUnavailable)]
    public async Task RejectWithoutDispatchWhenAuthenticationOrAcceptedRunIsMissing(string scenario, HttpStatusCode expected)
    {
        var model = new ModelClient();
        using var host = await CreateHostAsync(model, scenario == "missing_runtime" ? null : new RunAuthorizer());
        using var client = CreateClient(host, scenario != "unauthenticated");
        var request = ReadFixture();
        if (scenario == "foreign_run")
            request["input"]!["runId"] = Guid.NewGuid();
        if (scenario == "duplicate")
        {
            using var first = await client.PostAsync(SoloAgentModelEndpoint.Path, CreateContent(request), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }
        using var response = await client.PostAsync(SoloAgentModelEndpoint.Path, CreateContent(request), TestContext.Current.CancellationToken);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(scenario == "duplicate" ? 1 : 0, model.Calls);
    }

    [Theory]
    [InlineData("version", "unsupported_contract")]
    [InlineData("extra_field", "validation_failed")]
    [InlineData("missing_id", "validation_failed")]
    public async Task RejectWireIncompatibilityBeforeRuntimeOrModel(string scenario, string outcome)
    {
        var model = new ModelClient();
        var gate = new RunAuthorizer();
        using var host = await CreateHostAsync(model, gate);
        using var client = CreateClient(host);
        var request = ReadFixture();
        if (scenario == "version") request["contractVersion"] = 2;
        if (scenario == "extra_field") request["userId"] = Guid.NewGuid();
        if (scenario == "missing_id") request["input"]!.AsObject().Remove("chatId");
        using var response = await client.PostAsync(SoloAgentModelEndpoint.Path, CreateContent(request), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(outcome, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, model.Calls);
        Assert.Equal(0, gate.Calls);
    }

    [Fact]
    public async Task PreserveTechnicalModelFailureWithoutContentOrRawException()
    {
        var model = new ModelClient { Fail = true };
        using var host = await CreateHostAsync(model, new RunAuthorizer());
        using var client = CreateClient(host);
        using var response = await client.PostAsync(SoloAgentModelEndpoint.Path, CreateContent(ReadFixture()), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var reply = JsonSerializer.Deserialize<SoloAgentGenerationReply>(body, ModelProtocol.JsonOptions)!;
        Assert.Equal("provider_error", reply.Result!.Outcome);
        Assert.Null(reply.Result.Text);
        Assert.Null(reply.Result.Selection);
        Assert.DoesNotContain("PRIVATE-MODEL-ERROR", body);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public async Task RejectOversizedFullSnapshotBeforeAuthorizationOrDispatch()
    {
        var model = new ModelClient();
        var gate = new RunAuthorizer();
        using var host = await CreateHostAsync(model, gate, maximumBytes: 1);
        using var client = CreateClient(host);
        using var response = await client.PostAsync(SoloAgentModelEndpoint.Path, CreateContent(ReadFixture()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, gate.Calls);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task DeadlineAlsoBoundsRuntimeGate()
    {
        var model = new ModelClient();
        using var host = await CreateHostAsync(model, new RunAuthorizer { Stall = true }, timeout: TimeSpan.FromMilliseconds(100));
        using var client = CreateClient(host);
        using var response = await client.PostAsync(SoloAgentModelEndpoint.Path, CreateContent(ReadFixture()), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Contains("timeout", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, model.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectMessageReplayThroughRealRuntimeWithoutCallingModelAgain(bool newChat)
    {
        var model = new ModelClient();
        using var host = await CreateHostAsync(model, new SoloAgentRunAuthorizer());
        using var client = CreateClient(host);
        var request = ReadFixture();
        request["input"]!["history"] = new JsonArray();
        using var first = await client.PostAsync(SoloAgentModelEndpoint.Path, CreateContent(request), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        request["input"]!["requestId"] = Guid.NewGuid();
        request["input"]!["runId"] = Guid.NewGuid();
        if (newChat) request["input"]!["chatId"] = Guid.NewGuid();
        using var retry = await client.PostAsync(SoloAgentModelEndpoint.Path, CreateContent(request), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, retry.StatusCode);
        Assert.Equal(1, model.Calls);
    }

    private static Task<IHost> CreateHostAsync(ModelClient model, ISoloAgentRunAuthorizer? gate, long maximumBytes = 100_000, TimeSpan? timeout = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SoloAgentModelApi:Enabled"] = "true",
            ["SoloAgentModelApi:MaxRequestBytes"] = maximumBytes.ToString(),
            ["SoloAgentModelApi:MaxResponseBytes"] = "100000",
            ["SoloAgentModelApi:RequestTimeout"] = (timeout ?? TimeSpan.FromSeconds(5)).ToString(),
            ["SoloAgentModel:Enabled"] = "true",
            ["SoloAgentModel:MaxRequestBytes"] = "100000",
            ["SoloAgentModel:TotalDeadline"] = "00:00:05",
            ["SoloAgentModel:ModelTimeout"] = "00:00:04",
        }).Build();
        return new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer().ConfigureServices(services =>
        {
            services.AddRouting();
            services.AddAuthentication("test-service").AddScheme<AuthenticationSchemeOptions, ServiceAuthenticationHandler>("test-service", _ => { });
            services.AddAuthorization(options => options.AddPolicy("solo-backend", policy => policy.RequireAuthenticatedUser().RequireRole("solo-backend")));
            services.AddSoloAgentModelEndpoint(configuration);
            services.AddSingleton<IModelGenerationClient>(model);
            if (gate is not null) services.AddSingleton<ISoloAgentRunAuthorizer>(gate);
        }).Configure(app =>
        {
            app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
            app.UseEndpoints(endpoints => endpoints.MapSoloAgentModelEndpoint("solo-backend"));
        })).StartAsync(TestContext.Current.CancellationToken);
    }

    private static HttpClient CreateClient(IHost host, bool authenticated = true)
    {
        var client = host.GetTestClient();
        if (authenticated) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-service-credential");
        return client;
    }
    private static string FixturePath(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "SoloAgentFixtures", name);
    private static JsonNode ReadFixture() => JsonNode.Parse(File.ReadAllText(FixturePath("generation-v1.json")))!;
    private static StringContent CreateContent(JsonNode request) => new(request.ToJsonString(), Encoding.UTF8, "application/json");

    private sealed class ModelClient : IModelGenerationClient
    {
        public int Calls { get; private set; }
        public bool Fail { get; init; }
        public ModelGenerationRequest? Request { get; private set; }
        public Task<ModelGenerationResponse> GenerateAsync(ModelGenerationRequest request, CancellationToken cancellationToken)
        {
            Calls++; Request = request;
            if (Fail) throw new HttpRequestException("PRIVATE-MODEL-ERROR");
            return Task.FromResult(new ModelGenerationResponse(1, request.RequestId, "completed")
            {
                Content = """{"kind":"recommendation","candidate":{"formId":"20000000-0000-0000-0000-000000000001","presetId":"20000000-0000-0000-0000-000000000002","profileId":null},"reason":null,"candidates":[]}""",
            });
        }
    }

    private sealed class RunAuthorizer : ISoloAgentRunAuthorizer
    {
        public PreparedGenerationInput PrepareGeneration(PreparedGenerationInput input) => input;
        public void Complete(PreparedGenerationInput input, SoloAgentResponse? result) { }
        private readonly HashSet<Guid> _acceptedRequests = [];
        public int Calls { get; private set; }
        public bool Stall { get; init; }
        public Task<bool> AuthorizeAndAcceptAsync(ClaimsPrincipal sender, PreparedGenerationInput input, CancellationToken cancellationToken)
        {
            Calls++;
            if (Stall) return new TaskCompletionSource<bool>().Task;
            var valid = sender.IsInRole("solo-backend") && input.RunId == Guid.Parse("10000000-0000-0000-0000-000000000003") && _acceptedRequests.Add(input.RequestId);
            return Task.FromResult(valid);
        }
    }

    private sealed class ServiceAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.Authorization != "Bearer synthetic-service-credential") return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "20000000-0000-0000-0000-000000000001"), new Claim(ClaimTypes.Role, "solo-backend")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
